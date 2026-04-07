/*
 * Uno Platform Wayland Clipboard Helper
 * Persistent clipboard tracking — init once, events cached as they arrive.
 * Compile: gcc -shared -fPIC -o libuno-clipboard.so uno-clipboard.c $(pkg-config --cflags --libs wayland-client)
 */

#include <wayland-client.h>
#include <string.h>
#include <stdlib.h>
#include <unistd.h>

static void noop() {}

/* --- Global clipboard state (one per app) --- */

static struct wl_display *g_display;
static struct wl_data_device *g_device;
static struct wl_data_device_manager *g_manager;
static struct wl_data_offer *g_offer;
static char **g_mimes;
static int g_mc, g_mcap;
static int g_has_selection;

/* Source for copy */
static struct wl_data_source *g_source;
static char *g_copy_text;
static size_t g_copy_len;

/* --- Offer listener --- */

static void offer_offer(void *d, struct wl_data_offer *o, const char *m) {
    if (g_mc >= g_mcap) { g_mcap = g_mcap ? g_mcap*2 : 16; g_mimes = realloc(g_mimes, g_mcap*sizeof(char*)); }
    g_mimes[g_mc++] = strdup(m);
}
static const struct wl_data_offer_listener offer_listener = { .offer = offer_offer };

/* --- Device listener --- */

static void dev_data_offer(void *d, struct wl_data_device *dev, struct wl_data_offer *o) {
    /* New offer arriving — clear previous */
    if (g_offer) {
        wl_data_offer_destroy(g_offer);
        for (int i = 0; i < g_mc; i++) free(g_mimes[i]);
        g_mc = 0;
    }
    g_offer = o;
    g_has_selection = 0;
    wl_data_offer_add_listener(o, &offer_listener, NULL);
}

static void dev_selection(void *d, struct wl_data_device *dev, struct wl_data_offer *o) {
    g_has_selection = 1;
    if (o == NULL) {
        /* Clipboard cleared */
        if (g_offer) {
            wl_data_offer_destroy(g_offer);
            g_offer = NULL;
        }
        for (int i = 0; i < g_mc; i++) free(g_mimes[i]);
        g_mc = 0;
    }
}

static const struct wl_data_device_listener dev_listener = {
    .data_offer = dev_data_offer,
    .enter = (void*)noop, .leave = (void*)noop,
    .motion = (void*)noop, .drop = (void*)noop,
    .selection = dev_selection
};

/* --- Source listener (for copy) --- */

static void src_send(void *d, struct wl_data_source *src, const char *mime, int fd) {
    if (g_copy_text) {
        size_t w = 0;
        while (w < g_copy_len) {
            ssize_t n = write(fd, g_copy_text + w, g_copy_len - w);
            if (n <= 0) break;
            w += n;
        }
    }
    close(fd);
}

static void src_cancelled(void *d, struct wl_data_source *src) {
    if (src == g_source) {
        wl_data_source_destroy(g_source);
        g_source = NULL;
    }
}

static const struct wl_data_source_listener src_listener = {
    .target = (void*)noop, .send = src_send, .cancelled = src_cancelled
};

/* --- Registry --- */

static struct wl_seat *g_seat;

static void reg_global(void *d, struct wl_registry *r, uint32_t n, const char *i, uint32_t v) {
    if (!strcmp(i, "wl_seat") && !g_seat)
        g_seat = wl_registry_bind(r, n, &wl_seat_interface, 1);
    else if (!strcmp(i, "wl_data_device_manager") && !g_manager)
        g_manager = wl_registry_bind(r, n, &wl_data_device_manager_interface, 1);
}
static void reg_remove(void *d, struct wl_registry *r, uint32_t n) {}
static const struct wl_registry_listener reg_listener = { .global = reg_global, .global_remove = reg_remove };

/*
 * Initialize clipboard tracking. Call ONCE from the event thread.
 * After this, selection events are processed by the normal wl_display_dispatch loop.
 */
int uno_clipboard_init(struct wl_display *display) {
    if (g_device) return 0; /* already initialized */
    g_display = display;

    struct wl_registry *reg = wl_display_get_registry(display);
    wl_registry_add_listener(reg, &reg_listener, NULL);
    wl_display_roundtrip(display);

    if (!g_seat || !g_manager) return -1;

    g_device = wl_data_device_manager_get_data_device(g_manager, g_seat);
    wl_data_device_add_listener(g_device, &dev_listener, NULL);

    /* Roundtrip to receive initial selection */
    wl_display_roundtrip(display);

    return 0;
}

/*
 * Get clipboard text. Reads from the cached offer.
 * MUST be called from the event thread.
 * Returns malloc'd string (caller must free via uno_clipboard_free).
 */
char* uno_clipboard_get_text(void) {
    if (!g_offer || !g_has_selection || !g_display) return NULL;

    /* Find text mime */
    const char *mime = NULL;
    for (int i = 0; i < g_mc; i++) {
        if (!strcmp(g_mimes[i], "text/plain;charset=utf-8")) { mime = g_mimes[i]; break; }
        if (!strcmp(g_mimes[i], "text/plain") && !mime) mime = g_mimes[i];
    }
    if (!mime) return NULL;

    int fds[2];
    if (pipe(fds) < 0) return NULL;
    wl_data_offer_receive(g_offer, mime, fds[1]);
    wl_display_flush(g_display);
    close(fds[1]);

    char *result = NULL;
    size_t len = 0, cap = 0;
    char buf[4096]; ssize_t n;
    while ((n = read(fds[0], buf, sizeof(buf))) > 0) {
        if (len + n + 1 > cap) { cap = (len + n + 1) * 2; result = realloc(result, cap); }
        memcpy(result + len, buf, n); len += n;
    }
    close(fds[0]);
    if (result) result[len] = '\0';
    return result;
}

/*
 * Set clipboard text. Creates a data source and sets selection.
 * MUST be called from the event thread.
 * The text is copied internally.
 */
int uno_clipboard_set_text(const char *text, uint32_t serial) {
    if (!text || !g_manager || !g_device || !g_display) return -1;

    /* Destroy previous source */
    if (g_source) {
        wl_data_source_destroy(g_source);
        g_source = NULL;
    }
    free(g_copy_text);
    g_copy_text = strdup(text);
    g_copy_len = strlen(text);

    g_source = wl_data_device_manager_create_data_source(g_manager);
    wl_data_source_add_listener(g_source, &src_listener, NULL);
    wl_data_source_offer(g_source, "text/plain;charset=utf-8");
    wl_data_source_offer(g_source, "text/plain");
    wl_data_device_set_selection(g_device, g_source, serial);
    wl_display_flush(g_display);
    return 0;
}

void uno_clipboard_free(char *p) { free(p); }

/* Expose internal state for C# hybrid approach */
struct wl_data_device* uno_clipboard_get_device(void) { return g_device; }
struct wl_data_device_manager* uno_clipboard_get_manager(void) { return g_manager; }
struct wl_display* uno_clipboard_get_display(void) { return g_display; }

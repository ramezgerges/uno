/*
 * Uno Platform Wayland Clipboard Helper
 *
 * Native C library for clipboard via wl_data_device.
 * Uses wayland-scanner generated inline functions for correct protocol handling.
 *
 * - get_text_from_display: uses the app's existing display (which has focus)
 * - set_text: opens its own connection (doesn't need focus)
 *
 * Compile: gcc -shared -fPIC -o libuno-clipboard.so uno-clipboard.c $(pkg-config --cflags --libs wayland-client)
 */

#include <wayland-client.h>
#include <string.h>
#include <stdlib.h>
#include <stdio.h>
#include <unistd.h>
#include <fcntl.h>
#include <poll.h>

/* --- Paste state --- */

struct paste_state {
    struct wl_seat *seat;
    struct wl_data_device_manager *manager;
    struct wl_data_device *device;
    struct wl_data_offer *offer;
    char **mime_types;
    int mime_count;
    int mime_capacity;
    int selection_received;
};

static void ps_offer_offer(void *data, struct wl_data_offer *offer, const char *mime) {
    struct paste_state *s = data;
    if (s->mime_count >= s->mime_capacity) {
        s->mime_capacity = s->mime_capacity ? s->mime_capacity * 2 : 16;
        s->mime_types = realloc(s->mime_types, s->mime_capacity * sizeof(char*));
    }
    s->mime_types[s->mime_count++] = strdup(mime);
}

static const struct wl_data_offer_listener ps_offer_listener = {
    .offer = ps_offer_offer
};

static void ps_data_offer(void *data, struct wl_data_device *dev, struct wl_data_offer *offer) {
    struct paste_state *s = data;
    if (s->offer) {
        wl_data_offer_destroy(s->offer);
        for (int i = 0; i < s->mime_count; i++) free(s->mime_types[i]);
        s->mime_count = 0;
    }
    s->offer = offer;
    wl_data_offer_add_listener(offer, &ps_offer_listener, s);
}

static void ps_selection(void *data, struct wl_data_device *dev, struct wl_data_offer *offer) {
    struct paste_state *s = data;
    s->selection_received = 1;
}

static void ps_noop() {}

static const struct wl_data_device_listener ps_device_listener = {
    .data_offer = ps_data_offer,
    .enter = (void*)ps_noop,
    .leave = (void*)ps_noop,
    .motion = (void*)ps_noop,
    .drop = (void*)ps_noop,
    .selection = ps_selection
};

static void ps_registry_global(void *data, struct wl_registry *reg, uint32_t name, const char *iface, uint32_t ver) {
    struct paste_state *s = data;
    if (strcmp(iface, "wl_seat") == 0)
        s->seat = wl_registry_bind(reg, name, &wl_seat_interface, 1);
    else if (strcmp(iface, "wl_data_device_manager") == 0)
        s->manager = wl_registry_bind(reg, name, &wl_data_device_manager_interface, 1);
}

static void ps_registry_remove(void *data, struct wl_registry *reg, uint32_t name) {}

static const struct wl_registry_listener ps_registry_listener = {
    .global = ps_registry_global,
    .global_remove = ps_registry_remove
};

static char* read_offer(struct wl_data_offer *offer, const char *mime, struct wl_display *display) {
    int fds[2];
    if (pipe(fds) < 0) return NULL;
    wl_data_offer_receive(offer, mime, fds[1]);
    wl_display_flush(display);
    close(fds[1]);

    char *result = NULL;
    size_t len = 0, cap = 0;
    char buf[4096];
    ssize_t n;
    while ((n = read(fds[0], buf, sizeof(buf))) > 0) {
        if (len + n + 1 > cap) { cap = (len + n + 1) * 2; result = realloc(result, cap); }
        memcpy(result + len, buf, n);
        len += n;
    }
    close(fds[0]);
    if (result) result[len] = '\0';
    return result;
}

/*
 * Get clipboard text using the APP'S existing display connection.
 * The display MUST belong to a client that has keyboard focus.
 * WARNING: This function calls wl_display_roundtrip on the passed display,
 * so it must be called from the same thread that dispatches events for this display.
 */
char* uno_clipboard_get_text_from_display(struct wl_display *display) {
    if (!display) return NULL;

    struct paste_state state = {0};
    struct wl_registry *registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &ps_registry_listener, &state);
    wl_display_roundtrip(display);

    if (!state.seat || !state.manager) {
        wl_registry_destroy(registry);
        return NULL;
    }

    state.device = wl_data_device_manager_get_data_device(state.manager, state.seat);
    wl_data_device_add_listener(state.device, &ps_device_listener, &state);
    wl_display_roundtrip(display);

    char *result = NULL;
    if (state.offer && state.selection_received) {
        const char *mime = NULL;
        for (int i = 0; i < state.mime_count; i++) {
            if (strcmp(state.mime_types[i], "text/plain;charset=utf-8") == 0) { mime = state.mime_types[i]; break; }
            if (strcmp(state.mime_types[i], "text/plain") == 0 && !mime) mime = state.mime_types[i];
        }
        if (mime) result = read_offer(state.offer, mime, display);
    }

    for (int i = 0; i < state.mime_count; i++) free(state.mime_types[i]);
    free(state.mime_types);
    if (state.offer) wl_data_offer_destroy(state.offer);
    if (state.device) wl_data_device_destroy(state.device);
    if (state.manager) wl_data_device_manager_destroy(state.manager);
    if (state.seat) wl_seat_destroy(state.seat);
    wl_registry_destroy(registry);
    return result;
}

/* Legacy standalone version (opens own connection — no focus, may not work) */
char* uno_clipboard_get_text(void) {
    struct wl_display *display = wl_display_connect(NULL);
    if (!display) return NULL;
    char *result = uno_clipboard_get_text_from_display(display);
    wl_display_disconnect(display);
    return result;
}

/* --- Copy (set clipboard) --- */

struct copy_state {
    struct wl_seat *seat;
    struct wl_data_device_manager *manager;
    const char *text;
    size_t text_len;
    int cancelled;
};

static void cs_send(void *data, struct wl_data_source *source, const char *mime, int fd) {
    struct copy_state *s = data;
    size_t written = 0;
    while (written < s->text_len) {
        ssize_t n = write(fd, s->text + written, s->text_len - written);
        if (n <= 0) break;
        written += n;
    }
    close(fd);
}

static void cs_cancelled(void *data, struct wl_data_source *source) {
    struct copy_state *s = data;
    s->cancelled = 1;
}

static const struct wl_data_source_listener cs_source_listener = {
    .target = (void*)ps_noop,
    .send = cs_send,
    .cancelled = cs_cancelled
};

static void cs_registry_global(void *data, struct wl_registry *reg, uint32_t name, const char *iface, uint32_t ver) {
    struct copy_state *s = data;
    if (strcmp(iface, "wl_seat") == 0)
        s->seat = wl_registry_bind(reg, name, &wl_seat_interface, 1);
    else if (strcmp(iface, "wl_data_device_manager") == 0)
        s->manager = wl_registry_bind(reg, name, &wl_data_device_manager_interface, 1);
}

static void cs_registry_remove(void *data, struct wl_registry *reg, uint32_t name) {}

static const struct wl_registry_listener cs_registry_listener = {
    .global = cs_registry_global,
    .global_remove = cs_registry_remove
};

/* Set clipboard. Dispatches events until cancelled or timeout (30s). */
int uno_clipboard_set_text(const char *text) {
    if (!text) return -1;

    struct copy_state state = { .text = text, .text_len = strlen(text) };

    struct wl_display *display = wl_display_connect(NULL);
    if (!display) return -1;

    struct wl_registry *registry = wl_display_get_registry(display);
    wl_registry_add_listener(registry, &cs_registry_listener, &state);
    wl_display_roundtrip(display);

    if (!state.seat || !state.manager) {
        wl_registry_destroy(registry);
        wl_display_disconnect(display);
        return -1;
    }

    struct wl_data_device *device = wl_data_device_manager_get_data_device(state.manager, state.seat);
    struct wl_data_source *source = wl_data_device_manager_create_data_source(state.manager);
    wl_data_source_add_listener(source, &cs_source_listener, &state);
    wl_data_source_offer(source, "text/plain;charset=utf-8");
    wl_data_source_offer(source, "text/plain");
    wl_data_device_set_selection(device, source, 0);
    wl_display_flush(display);

    /* Dispatch events to serve paste requests from other apps */
    struct pollfd pfd = { .fd = wl_display_get_fd(display), .events = POLLIN };
    for (int i = 0; i < 300 && !state.cancelled; i++) {
        wl_display_flush(display);
        if (poll(&pfd, 1, 100) > 0) wl_display_dispatch(display);
    }

    wl_data_source_destroy(source);
    wl_data_device_destroy(device);
    wl_data_device_manager_destroy(state.manager);
    wl_seat_destroy(state.seat);
    wl_registry_destroy(registry);
    wl_display_disconnect(display);
    return 0;
}

void uno_clipboard_free(char *ptr) { free(ptr); }

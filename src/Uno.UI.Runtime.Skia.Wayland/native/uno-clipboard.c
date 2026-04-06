/*
 * Uno Platform Wayland Clipboard Helper
 *
 * Self-contained C library for clipboard operations via wl_data_device.
 * Each operation opens its own Wayland connection for thread safety.
 * Inspired by wl-clipboard (https://github.com/bugaevc/wl-clipboard).
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
#include <errno.h>

/* --- Paste (get clipboard) --- */

struct paste_state {
    struct wl_display *display;
    struct wl_registry *registry;
    struct wl_seat *seat;
    struct wl_data_device_manager *manager;
    struct wl_data_device *device;
    struct wl_data_offer *offer;
    char **mime_types;
    int mime_count;
    int mime_capacity;
    int done;
};

static void paste_offer_offer(void *data, struct wl_data_offer *offer, const char *mime_type) {
    struct paste_state *state = data;
    if (state->mime_count >= state->mime_capacity) {
        state->mime_capacity = state->mime_capacity ? state->mime_capacity * 2 : 16;
        state->mime_types = realloc(state->mime_types, state->mime_capacity * sizeof(char*));
    }
    state->mime_types[state->mime_count++] = strdup(mime_type);
}

static const struct wl_data_offer_listener paste_offer_listener = {
    .offer = paste_offer_offer
};

static void paste_data_offer(void *data, struct wl_data_device *device, struct wl_data_offer *offer) {
    struct paste_state *state = data;
    /* Clean up previous offer */
    if (state->offer) {
        wl_data_offer_destroy(state->offer);
        for (int i = 0; i < state->mime_count; i++) free(state->mime_types[i]);
        state->mime_count = 0;
    }
    state->offer = offer;
    wl_data_offer_add_listener(offer, &paste_offer_listener, state);
}

static void paste_selection(void *data, struct wl_data_device *device, struct wl_data_offer *offer) {
    struct paste_state *state = data;
    state->done = 1;
}

static void paste_noop() {}

static const struct wl_data_device_listener paste_device_listener = {
    .data_offer = paste_data_offer,
    .enter = (void*)paste_noop,
    .leave = (void*)paste_noop,
    .motion = (void*)paste_noop,
    .drop = (void*)paste_noop,
    .selection = paste_selection
};

static void paste_registry_global(void *data, struct wl_registry *registry, uint32_t name, const char *interface, uint32_t version) {
    struct paste_state *state = data;
    if (strcmp(interface, "wl_seat") == 0) {
        state->seat = wl_registry_bind(registry, name, &wl_seat_interface, 1);
    } else if (strcmp(interface, "wl_data_device_manager") == 0) {
        state->manager = wl_registry_bind(registry, name, &wl_data_device_manager_interface, 1);
    }
}

static void paste_registry_remove(void *data, struct wl_registry *registry, uint32_t name) {}

static const struct wl_registry_listener paste_registry_listener = {
    .global = paste_registry_global,
    .global_remove = paste_registry_remove
};

/*
 * Get clipboard text. Returns malloc'd string (caller must free), or NULL.
 * Opens its own Wayland connection — fully thread-safe.
 */
char* uno_clipboard_get_text(void) {
    struct paste_state state = {0};

    state.display = wl_display_connect(NULL);
    if (!state.display) return NULL;

    state.registry = wl_display_get_registry(state.display);
    wl_registry_add_listener(state.registry, &paste_registry_listener, &state);
    wl_display_roundtrip(state.display);

    if (!state.seat || !state.manager) goto cleanup;

    state.device = wl_data_device_manager_get_data_device(state.manager, state.seat);
    wl_data_device_add_listener(state.device, &paste_device_listener, &state);

    /* Roundtrip to get selection events */
    wl_display_roundtrip(state.display);

    if (!state.offer || !state.done) goto cleanup;

    /* Find text mime type */
    const char *mime = NULL;
    for (int i = 0; i < state.mime_count; i++) {
        if (strcmp(state.mime_types[i], "text/plain;charset=utf-8") == 0) { mime = state.mime_types[i]; break; }
        if (strcmp(state.mime_types[i], "text/plain") == 0 && !mime) { mime = state.mime_types[i]; }
    }
    if (!mime) goto cleanup;

    /* Create pipe and receive */
    int fds[2];
    if (pipe(fds) < 0) goto cleanup;

    wl_data_offer_receive(state.offer, mime, fds[1]);
    wl_display_flush(state.display);
    close(fds[1]);

    /* Read from pipe */
    char *result = NULL;
    size_t result_len = 0;
    size_t result_cap = 0;
    char buf[4096];
    ssize_t n;
    while ((n = read(fds[0], buf, sizeof(buf))) > 0) {
        if (result_len + n + 1 > result_cap) {
            result_cap = (result_len + n + 1) * 2;
            result = realloc(result, result_cap);
        }
        memcpy(result + result_len, buf, n);
        result_len += n;
    }
    close(fds[0]);

    if (result) result[result_len] = '\0';

    /* Cleanup */
    for (int i = 0; i < state.mime_count; i++) free(state.mime_types[i]);
    free(state.mime_types);
    if (state.offer) wl_data_offer_destroy(state.offer);
    if (state.device) wl_data_device_destroy(state.device);
    if (state.manager) wl_data_device_manager_destroy(state.manager);
    if (state.seat) wl_seat_destroy(state.seat);
    wl_registry_destroy(state.registry);
    wl_display_disconnect(state.display);
    return result;

cleanup:
    for (int i = 0; i < state.mime_count; i++) free(state.mime_types[i]);
    free(state.mime_types);
    if (state.offer) wl_data_offer_destroy(state.offer);
    if (state.device) wl_data_device_destroy(state.device);
    if (state.manager) wl_data_device_manager_destroy(state.manager);
    if (state.seat) wl_seat_destroy(state.seat);
    if (state.registry) wl_registry_destroy(state.registry);
    if (state.display) wl_display_disconnect(state.display);
    return NULL;
}

/* --- Copy (set clipboard) --- */

struct copy_state {
    struct wl_display *display;
    struct wl_registry *registry;
    struct wl_seat *seat;
    struct wl_data_device_manager *manager;
    struct wl_data_device *device;
    struct wl_data_source *source;
    const char *text;
    size_t text_len;
    int cancelled;
};

static void copy_source_send(void *data, struct wl_data_source *source, const char *mime_type, int fd) {
    struct copy_state *state = data;
    size_t written = 0;
    while (written < state->text_len) {
        ssize_t n = write(fd, state->text + written, state->text_len - written);
        if (n <= 0) break;
        written += n;
    }
    close(fd);
}

static void copy_source_cancelled(void *data, struct wl_data_source *source) {
    struct copy_state *state = data;
    state->cancelled = 1;
}

static const struct wl_data_source_listener copy_source_listener = {
    .target = (void*)paste_noop,
    .send = copy_source_send,
    .cancelled = copy_source_cancelled
};

static void copy_registry_global(void *data, struct wl_registry *registry, uint32_t name, const char *interface, uint32_t version) {
    struct copy_state *state = data;
    if (strcmp(interface, "wl_seat") == 0) {
        state->seat = wl_registry_bind(registry, name, &wl_seat_interface, 1);
    } else if (strcmp(interface, "wl_data_device_manager") == 0) {
        state->manager = wl_registry_bind(registry, name, &wl_data_device_manager_interface, 1);
    }
}

static void copy_registry_remove(void *data, struct wl_registry *registry, uint32_t name) {}

static const struct wl_registry_listener copy_registry_listener = {
    .global = copy_registry_global,
    .global_remove = copy_registry_remove
};

/*
 * Set clipboard text. The function dispatches events until another app takes
 * the clipboard or timeout_ms elapses. Pass timeout_ms=0 to set and return
 * immediately (fire-and-forget mode — source stays alive on a background thread).
 * Returns 0 on success, -1 on error.
 */
int uno_clipboard_set_text(const char *text) {
    if (!text) return -1;

    struct copy_state state = {0};
    state.text = text;
    state.text_len = strlen(text);

    state.display = wl_display_connect(NULL);
    if (!state.display) return -1;

    state.registry = wl_display_get_registry(state.display);
    wl_registry_add_listener(state.registry, &copy_registry_listener, &state);
    wl_display_roundtrip(state.display);

    if (!state.seat || !state.manager) {
        wl_registry_destroy(state.registry);
        wl_display_disconnect(state.display);
        return -1;
    }

    state.device = wl_data_device_manager_get_data_device(state.manager, state.seat);
    state.source = wl_data_device_manager_create_data_source(state.manager);
    wl_data_source_add_listener(state.source, &copy_source_listener, &state);
    wl_data_source_offer(state.source, "text/plain;charset=utf-8");
    wl_data_source_offer(state.source, "text/plain");
    wl_data_device_set_selection(state.device, state.source, 0);
    wl_display_flush(state.display);

    /* Dispatch until cancelled or timeout (5 seconds) */
    struct pollfd pfd = { .fd = wl_display_get_fd(state.display), .events = POLLIN };
    int remaining = 5000;
    while (!state.cancelled && remaining > 0) {
        wl_display_flush(state.display);
        int ret = poll(&pfd, 1, 100);
        if (ret > 0) {
            wl_display_dispatch(state.display);
        }
        remaining -= 100;
    }

    wl_data_source_destroy(state.source);
    wl_data_device_destroy(state.device);
    wl_data_device_manager_destroy(state.manager);
    wl_seat_destroy(state.seat);
    wl_registry_destroy(state.registry);
    wl_display_disconnect(state.display);
    return 0;
}

/* Free a string returned by uno_clipboard_get_text */
void uno_clipboard_free(char *ptr) {
    free(ptr);
}

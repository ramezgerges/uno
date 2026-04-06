/*
 * Uno Platform Wayland Clipboard Helper
 * Compile: gcc -shared -fPIC -o libuno-clipboard.so uno-clipboard.c $(pkg-config --cflags --libs wayland-client)
 */

#include <wayland-client.h>
#include <string.h>
#include <stdlib.h>
#include <unistd.h>
#include <poll.h>

static void noop() {}

/* --- Paste --- */

struct paste_state {
    struct wl_seat *seat;
    struct wl_data_device_manager *manager;
    struct wl_data_offer *offer;
    char **mimes; int mc, mcap;
    int done;
};

static void po_offer(void *d, struct wl_data_offer *o, const char *m) {
    struct paste_state *s = d;
    if (s->mc >= s->mcap) { s->mcap = s->mcap ? s->mcap*2 : 16; s->mimes = realloc(s->mimes, s->mcap*sizeof(char*)); }
    s->mimes[s->mc++] = strdup(m);
}
static const struct wl_data_offer_listener po_l = { .offer = po_offer };

static void pd_offer(void *d, struct wl_data_device *dev, struct wl_data_offer *o) {
    struct paste_state *s = d;
    if (s->offer) { wl_data_offer_destroy(s->offer); for (int i=0;i<s->mc;i++) free(s->mimes[i]); s->mc=0; }
    s->offer = o;
    wl_data_offer_add_listener(o, &po_l, s);
}
static void pd_sel(void *d, struct wl_data_device *dev, struct wl_data_offer *o) { ((struct paste_state*)d)->done=1; }
static const struct wl_data_device_listener pd_l = {
    .data_offer=pd_offer, .enter=(void*)noop, .leave=(void*)noop,
    .motion=(void*)noop, .drop=(void*)noop, .selection=pd_sel
};
static void pr_g(void *d, struct wl_registry *r, uint32_t n, const char *i, uint32_t v) {
    struct paste_state *s = d;
    if (!strcmp(i,"wl_seat")) s->seat = wl_registry_bind(r,n,&wl_seat_interface,1);
    else if (!strcmp(i,"wl_data_device_manager")) s->manager = wl_registry_bind(r,n,&wl_data_device_manager_interface,1);
}
static void pr_r(void *d, struct wl_registry *r, uint32_t n) {}
static const struct wl_registry_listener pr_l = { .global=pr_g, .global_remove=pr_r };

/* Paste using app's display. MUST be called from the event thread. */
char* uno_clipboard_get_text_from_display(struct wl_display *display) {
    if (!display) return NULL;
    struct paste_state s = {0};
    struct wl_registry *reg = wl_display_get_registry(display);
    wl_registry_add_listener(reg, &pr_l, &s);
    wl_display_roundtrip(display);
    if (!s.seat || !s.manager) { wl_registry_destroy(reg); return NULL; }

    struct wl_data_device *dev = wl_data_device_manager_get_data_device(s.manager, s.seat);
    wl_data_device_add_listener(dev, &pd_l, &s);
    wl_display_roundtrip(display);
    if (!s.done) wl_display_roundtrip(display);

    char *result = NULL;
    if (s.offer && s.done) {
        const char *mime = NULL;
        for (int i=0; i<s.mc; i++) {
            if (!strcmp(s.mimes[i],"text/plain;charset=utf-8")) { mime=s.mimes[i]; break; }
            if (!strcmp(s.mimes[i],"text/plain") && !mime) mime=s.mimes[i];
        }
        if (mime) {
            int fds[2];
            if (pipe(fds)==0) {
                wl_data_offer_receive(s.offer, mime, fds[1]);
                wl_display_flush(display);
                close(fds[1]);
                char buf[4096]; ssize_t n; size_t len=0,cap=0;
                while ((n=read(fds[0],buf,sizeof(buf)))>0) {
                    if (len+n+1>cap) { cap=(len+n+1)*2; result=realloc(result,cap); }
                    memcpy(result+len,buf,n); len+=n;
                }
                close(fds[0]);
                if (result) result[len]='\0';
            }
        }
    }
    for (int i=0;i<s.mc;i++) free(s.mimes[i]);
    free(s.mimes);
    if (s.offer) wl_data_offer_destroy(s.offer);
    wl_data_device_destroy(dev);
    wl_data_device_manager_destroy(s.manager);
    wl_seat_destroy(s.seat);
    wl_registry_destroy(reg);
    return result;
}

/* --- Copy: set selection on the app's display. Returns source pointer (caller manages lifetime). */

struct copy_ctx {
    const char *text; size_t len;
};

static void cs_send(void *d, struct wl_data_source *src, const char *mime, int fd) {
    struct copy_ctx *c = d;
    size_t w=0;
    while (w < c->len) { ssize_t n=write(fd,c->text+w,c->len-w); if(n<=0)break; w+=n; }
    close(fd);
}
static void cs_cancel(void *d, struct wl_data_source *src) {}
static const struct wl_data_source_listener cs_l = {
    .target=(void*)noop, .send=cs_send, .cancelled=cs_cancel
};

/*
 * Set clipboard on the app's display. MUST be called from event thread.
 * Returns a wl_data_source* that the caller must keep alive (and eventually destroy)
 * until cancelled. The ctx must also stay alive.
 *
 * Parameters:
 *   display - app's wl_display (has focus)
 *   text    - text to copy (must stay valid until source is destroyed)
 *   serial  - serial from a recent input event
 *   out_ctx - receives a malloc'd context pointer (caller must free after destroying source)
 *
 * Returns: wl_data_source pointer, or NULL on failure.
 */
struct wl_data_source* uno_clipboard_set_selection(
    struct wl_display *display, const char *text, uint32_t serial, void **out_ctx
) {
    if (!display || !text) return NULL;
    *out_ctx = NULL;

    /* Bind manager and seat */
    struct paste_state s = {0}; /* reuse paste_state for registry */
    struct wl_registry *reg = wl_display_get_registry(display);
    wl_registry_add_listener(reg, &pr_l, &s);
    wl_display_roundtrip(display);
    if (!s.seat || !s.manager) { wl_registry_destroy(reg); return NULL; }

    struct copy_ctx *ctx = malloc(sizeof(struct copy_ctx));
    ctx->text = text;
    ctx->len = strlen(text);

    struct wl_data_device *dev = wl_data_device_manager_get_data_device(s.manager, s.seat);
    struct wl_data_source *src = wl_data_device_manager_create_data_source(s.manager);
    wl_data_source_add_listener(src, &cs_l, ctx);
    wl_data_source_offer(src, "text/plain;charset=utf-8");
    wl_data_source_offer(src, "text/plain");
    wl_data_device_set_selection(dev, src, serial);
    wl_display_flush(display);

    /* Clean up bindings (but NOT the source — caller owns it) */
    wl_data_device_destroy(dev);
    wl_data_device_manager_destroy(s.manager);
    wl_seat_destroy(s.seat);
    wl_registry_destroy(reg);

    *out_ctx = ctx;
    return src;
}

void uno_clipboard_destroy_source(struct wl_data_source *src, void *ctx) {
    if (src) wl_data_source_destroy(src);
    free(ctx);
}

void uno_clipboard_free(char *p) { free(p); }

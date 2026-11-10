#include <gtk/gtk.h>
#include <gdk/gdkwayland.h>
#include <gdk/gdkx.h>
#include <webkit2/webkit2.h>
#include <gtk-layer-shell.h>
#include <string>
#include <cstring>

// Called on Photino's GTK main thread. Independent overlay surfaces never take input focus.
namespace {
GtkWidget* window = nullptr;
WebKitWebView* webview = nullptr;
std::string script;
guint timer = 0;
GdkDisplay* xdisplay = nullptr;
void clear() {
    if (timer) { g_source_remove(timer); timer = 0; }
    auto previous = window; window = nullptr; webview = nullptr;
    if (previous) gtk_widget_destroy(previous);
}
void loaded(WebKitWebView* view, WebKitLoadEvent event, gpointer) {
    if (view == webview && event == WEBKIT_LOAD_FINISHED) webkit_web_view_evaluate_javascript(view, script.c_str(), -1, nullptr, nullptr, nullptr, nullptr, nullptr);
}
void click_through(GtkWidget* widget, gpointer) {
    auto region = cairo_region_create(); gtk_widget_input_shape_combine_region(widget, region); cairo_region_destroy(region);
}
}
extern "C" __attribute__((visibility("default"))) void wfc_overlay_hide() noexcept { clear(); }
extern "C" __attribute__((visibility("default"))) void wfc_overlay_update(const char* payload) noexcept {
    try { if (!webview || !payload || std::strlen(payload)>1024*1024) return; script="(()=>{const d="+std::string(payload)+";window.render(d.kind,d.result,d.unit,d.boxes);})();"; webkit_web_view_evaluate_javascript(webview,script.c_str(),-1,nullptr,nullptr,nullptr,nullptr,nullptr); } catch (...) { }
}
extern "C" __attribute__((visibility("default"))) int wfc_overlay_show(const char* url, const char* payload, int x, int y, int width, int height, int seconds, int backend) noexcept {
    try {
        if (!url || !payload || std::strlen(payload) > 1024*1024 || width <= 0 || height <= 0 || width > 16384 || height > 16384) return -1;
        auto display = gdk_display_get_default(); if (!display) return -2;
        auto wayland = GDK_IS_WAYLAND_DISPLAY(display);
        if (backend == 1 && (!wayland || !gtk_layer_is_supported())) return -3;
        if (wayland && (backend == 2 || !gtk_layer_is_supported())) {
            if (!xdisplay) { const char* name = g_getenv("DISPLAY"); if (name && *name) xdisplay = gdk_display_open(name); }
            if (!xdisplay || !GDK_IS_X11_DISPLAY(xdisplay)) return -3;
            display = xdisplay; wayland = false;
        }
        clear(); window = gtk_window_new(GTK_WINDOW_TOPLEVEL);
        gtk_window_set_screen(GTK_WINDOW(window), gdk_display_get_default_screen(display));
        gtk_window_set_title(GTK_WINDOW(window), "Warframe Companion Overlay");
        gtk_window_set_decorated(GTK_WINDOW(window), FALSE); gtk_window_set_resizable(GTK_WINDOW(window), FALSE);
        gtk_window_set_accept_focus(GTK_WINDOW(window), FALSE); gtk_window_set_focus_on_map(GTK_WINDOW(window), FALSE);
        gtk_window_set_skip_taskbar_hint(GTK_WINDOW(window), TRUE); gtk_window_set_skip_pager_hint(GTK_WINDOW(window), TRUE);
        gtk_window_set_keep_above(GTK_WINDOW(window), TRUE); gtk_widget_set_app_paintable(window, TRUE);
        auto visual = gdk_screen_get_rgba_visual(gtk_widget_get_screen(window)); if (visual) gtk_widget_set_visual(window, visual);
        auto monitor = gdk_display_get_monitor_at_point(display, x + width/2, y + height/2);
        if (wayland) {
            GdkRectangle geometry{}; if (monitor) gdk_monitor_get_geometry(monitor, &geometry);
            gtk_layer_init_for_window(GTK_WINDOW(window)); gtk_layer_set_namespace(GTK_WINDOW(window), "warframe-companion-overlay");
            gtk_layer_set_layer(GTK_WINDOW(window), GTK_LAYER_SHELL_LAYER_OVERLAY);
            gtk_layer_set_keyboard_mode(GTK_WINDOW(window), GTK_LAYER_SHELL_KEYBOARD_MODE_NONE);
            gtk_layer_set_exclusive_zone(GTK_WINDOW(window), -1); if (monitor) gtk_layer_set_monitor(GTK_WINDOW(window), monitor);
            gtk_layer_set_anchor(GTK_WINDOW(window), GTK_LAYER_SHELL_EDGE_LEFT, TRUE); gtk_layer_set_anchor(GTK_WINDOW(window), GTK_LAYER_SHELL_EDGE_TOP, TRUE);
            gtk_layer_set_margin(GTK_WINDOW(window), GTK_LAYER_SHELL_EDGE_LEFT, x - geometry.x);
            gtk_layer_set_margin(GTK_WINDOW(window), GTK_LAYER_SHELL_EDGE_TOP, y - geometry.y);
        } else gtk_window_move(GTK_WINDOW(window), x, y);
        gtk_window_set_default_size(GTK_WINDOW(window), width, height);
        webview = WEBKIT_WEB_VIEW(webkit_web_view_new()); GdkRGBA transparent{0,0,0,0}; webkit_web_view_set_background_color(webview, &transparent);
        gtk_widget_set_can_focus(GTK_WIDGET(webview), FALSE); gtk_container_add(GTK_CONTAINER(window), GTK_WIDGET(webview));
        script = "(()=>{const d=" + std::string(payload) + ";window.render(d.kind,d.result,d.unit,d.boxes);})();";
        // The managed adapter converts screenshot pixels into compositor logical coordinates.
        webkit_web_view_set_zoom_level(webview, 1.0);
        g_signal_connect(webview, "load-changed", G_CALLBACK(loaded), nullptr); g_signal_connect(window, "realize", G_CALLBACK(click_through), nullptr);
        gtk_widget_show_all(window); click_through(window, nullptr); webkit_web_view_load_uri(webview, url);
        timer = g_timeout_add_seconds(static_cast<guint>(seconds < 3 ? 3 : seconds > 300 ? 300 : seconds), [](gpointer) -> gboolean { timer = 0; clear(); return G_SOURCE_REMOVE; }, nullptr);
        return 0;
    } catch (...) { clear(); return -4; }
}

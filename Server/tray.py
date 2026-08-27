#!/usr/bin/env python3
"""PhonesBridge system tray icon — direct StatusNotifierItem over DBus.

Implemented directly against the org.kde.StatusNotifierItem + com.canonical.dbusmenu
protocols (no libappindicator / no icon-theme lookup) so the icon is delivered as an
ARGB32 pixmap in the DBus message and always renders on KDE Plasma.

Menu:
  🌐 Open Server   → opens the Web UI in the default browser
  🛑 Stop the server → SIGTERM the server (graceful shutdown)

The popup is placed exactly at the tray icon position (using the coordinates the
desktop sends over DBus) and closes automatically when you click anywhere else.

The tray auto-quits if the server process dies. Falls back to the GTK AppIndicator
path if the DBus stack is unavailable.
"""
import argparse
import os
import signal
import subprocess
import sys

# The context menu is shown with GTK. On a Wayland session KWin suppresses popups
# that aren't triggered by a real input event (our clicks arrive over DBus), so we
# force the X11 (XWayland) backend where pointer grabs and override-redirect menus
# always work. This must be a hard assignment (not setdefault) so it wins over a
# GDK_BACKEND=wayland inherited from the environment. Harmless fallback: if no X
# display exists, GTK simply isn't used and the SNI icon + dbusmenu keep working.
os.environ["GDK_BACKEND"] = "x11"

p = argparse.ArgumentParser()
p.add_argument("--pid", type=int, required=True)
p.add_argument("--port", type=int, default=8052)
p.add_argument("--title", default="PhonesBridge")
p.add_argument("--icon", default="")
args = p.parse_args()

URL = "http://127.0.0.1:%d" % args.port
ITEM_ID = "com.phonesbridge.tray"


def shell_open():
    for cmd in (["xdg-open", URL], ["gio", "open", URL], ["kde-open", URL]):
        try:
            subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            return
        except Exception:
            continue


def stop_server():
    try:
        os.kill(args.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass


def server_alive():
    try:
        os.kill(args.pid, 0)
        return True
    except (ProcessLookupError, PermissionError):
        return False


# ── Icon → ARGB32 pixmap (SNI IconPixmap) ────────────────────────────────────
def load_argb_pixmap(path, size=48):
    """Return [[width, height, ARGB bytes]] for the SNI IconPixmap property."""
    try:
        import gi
        gi.require_version("GdkPixbuf", "2.0")
        from gi.repository import GdkPixbuf
        pb = GdkPixbuf.Pixbuf.new_from_file_at_size(path, size, size)
    except Exception:
        return []
    w, h = pb.get_width(), pb.get_height()
    raw = bytes(pb.get_pixels())
    stride = pb.get_rowstride()
    ch = pb.get_n_channels()
    out = bytearray(w * h * 4)
    for y in range(h):
        row = y * stride
        orow = y * w * 4
        for x in range(w):
            i = row + x * ch
            r = raw[i]
            g = raw[i + 1]
            b = raw[i + 2]
            a = raw[i + 3] if ch >= 4 else 255
            o = orow + x * 4
            out[o] = a
            out[o + 1] = r
            out[o + 2] = g
            out[o + 3] = b
    return [[w, h, bytes(out)]]


# ── Direct SNI over DBus ─────────────────────────────────────────────────────
def run_sni():
    try:
        from dbus.mainloop.glib import DBusGMainLoop
        DBusGMainLoop(set_as_default=True)
        import dbus
        import dbus.service
        from gi.repository import GLib
    except Exception as e:
        sys.stderr.write("tray: dbus stack unavailable (%s)\n" % e)
        return False

    pixmap = load_argb_pixmap(args.icon) if args.icon else []

    class PropsObject(dbus.service.Object):
        def __init__(self, conn, path, props):
            super().__init__(conn, path)
            self._props = props

        @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ss", out_signature="v")
        def Get(self, interface, name):
            if interface in self._props and name in self._props[interface]:
                return self._props[interface][name]
            raise dbus.exceptions.DBusException(
                "No such property '%s' on interface '%s'" % (name, interface),
                name="org.freedesktop.DBus.Properties.NotFound")

        @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="s", out_signature="a{sv}")
        def GetAll(self, interface):
            return self._props.get(interface, {})

        @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ssv", out_signature="")
        def Set(self, interface, name, value):
            raise dbus.exceptions.DBusException(
                "Property '%s' is read-only" % name,
                name="org.freedesktop.DBus.Error.PropertyReadOnly")

    try:
        bus = dbus.SessionBus()
    except Exception as e:
        sys.stderr.write("tray: cannot open session bus (%s)\n" % e)
        return False

    if not bus.name_has_owner("org.kde.StatusNotifierWatcher"):
        sys.stderr.write("tray: no StatusNotifierWatcher on the session bus (is the desktop running?)\n")
        return False

    loop = GLib.MainLoop()

    # ── GTK context menu (shown on click) ─────────────────────────────────────
    # In Plasma the click calls our Activate/ContextMenu methods; a working menu
    # is what lets the user reach "Stop the server". GTK is used only for the
    # popup itself — the icon is still delivered as a pixmap over DBus.
    gtk_ok = False
    gtk_menu = None
    icon_x = 0
    icon_y = 0

    def popup_menu():
        if gtk_menu is not None:
            try:
                # Anchor the menu at the tray icon. The pointer sits exactly on
                # the icon when the user clicks, so we use the pointer position
                # (its X11 screen coordinates) rather than the coordinates the
                # desktop sent over DBus, which can be in a different space.
                px, py = icon_x, icon_y
                try:
                    import gi as _gi2
                    _gi2.require_version("Gdk", "3.0")
                    from gi.repository import Gdk as _Gdk2
                    seat = _Gdk2.Display.get_default().get_default_seat()
                    _scr, px, py = seat.get_pointer().get_position()
                except Exception:
                    pass

                def _pos_func(_menu, _data=None):
                    return (px, py, True)

                # popup() + a position function needs no GdkWindow and no
                # trigger event (clicks arrive over DBus). GDK_BACKEND=x11 (set
                # above) is what lets the grab engage, so any click outside the
                # menu dismisses it.
                gtk_menu.popup(None, None, _pos_func, None, 3,
                               _Gtk.get_current_event_time())
            except Exception:
                pass

    try:
        import gi as _gi
        _gi.require_version("Gtk", "3.0")
        from gi.repository import Gtk as _Gtk
        _Gtk.init(None)
        gtk_menu = _Gtk.Menu()
        mi = _Gtk.MenuItem(label="🌐 Open Server (%s)" % URL)
        mi.connect("activate", lambda *a: shell_open())
        gtk_menu.append(mi)
        gtk_menu.append(_Gtk.SeparatorMenuItem())
        mi = _Gtk.MenuItem(label="🛑 Stop the server")
        mi.connect("activate", lambda *a: (stop_server(), quit_app()))
        gtk_menu.append(mi)
        gtk_menu.show_all()
        gtk_ok = True
    except Exception:
        gtk_menu = None

    def quit_app():
        if gtk_ok:
            _Gtk.main_quit()
        else:
            loop.quit()

    def show_menu(x, y):
        nonlocal icon_x, icon_y
        try:
            icon_x = int(x)
            icon_y = int(y)
        except Exception:
            pass
        popup_menu()

    # Register using our unique name → the object lives at /StatusNotifierItem
    # (the spec's rule for ':'-prefixed services), avoiding name ownership and
    # keeping the object path DBus-valid (no dashes).
    item_path = "/StatusNotifierItem"
    menu_path = item_path + "/Menu"

    # ── Menu items ──
    MENU_OPEN = 1
    MENU_STOP = 2
    items = {
        MENU_OPEN: {"label": "🌐 Open Server (%s)" % URL, "type": "standard", "enabled": True, "visible": True},
        MENU_STOP: {"label": "🛑 Stop the server", "type": "standard", "enabled": True, "visible": True},
    }

    def handle_menu_event(item_id, event_id, data, timestamp):
        if event_id != "clicked":
            return
        if item_id == MENU_OPEN:
            shell_open()
        elif item_id == MENU_STOP:
            stop_server()
            GLib.idle_add(quit_app)

    class MenuObject(PropsObject):
        def __init__(self, conn, path):
            props = {
                "com.canonical.dbusmenu": {
                    "Version": dbus.UInt32(3),
                    "Status": "normal",
                    "TextDirection": "ltr",
                }
            }
            super().__init__(conn, path, props)

        @dbus.service.method("com.canonical.dbusmenu", in_signature="iias", out_signature="is")
        def GetLayout(self, parent_id, recursion_depth, property_names):
            layout = "<layout><item id='0'><property name='label'>root</property>"
            for iid in (MENU_OPEN, MENU_STOP):
                it = items[iid]
                layout += "<item id='%d'><property name='label'>%s</property>" % (iid, it["label"])
                layout += "<property name='type'>%s</property>" % it["type"]
                layout += "<property name='visible'>%s</property>" % ("true" if it["visible"] else "false")
                layout += "<property name='enabled'>%s</property>" % ("true" if it["enabled"] else "false")
                layout += "</item>"
            layout += "</item></layout>"
            return (1, layout)

        @dbus.service.method("com.canonical.dbusmenu", in_signature="ai", out_signature="a(ia{sv})")
        def GetGroupProperties(self, ids):
            result = []
            for iid in (MENU_OPEN, MENU_STOP):
                if not ids or iid in ids:
                    it = items[iid]
                    props = {
                        "label": it["label"],
                        "type": it["type"],
                        "enabled": it["enabled"],
                        "visible": it["visible"],
                    }
                    result.append((iid, props))
            return result

        @dbus.service.method("com.canonical.dbusmenu", in_signature="is", out_signature="v")
        def GetProperty(self, item_id, name):
            if item_id in items and name in items[item_id]:
                return items[item_id][name]
            raise dbus.exceptions.DBusException("Property not found",
                                                name="org.freedesktop.DBus.Error.UnknownProperty")

        @dbus.service.method("com.canonical.dbusmenu", in_signature="i", out_signature="b")
        def AboutToShow(self, item_id):
            return False

        @dbus.service.method("com.canonical.dbusmenu", in_signature="isvu", out_signature="")
        def Event(self, item_id, event_id, data, timestamp):
            handle_menu_event(int(item_id), str(event_id), data, int(timestamp))

        @dbus.service.method("com.canonical.dbusmenu", in_signature="a(isvu)", out_signature="")
        def EventGroup(self, events):
            for ev in events:
                handle_menu_event(int(ev[0]), str(ev[1]), ev[2], int(ev[3]))

        @dbus.service.method("com.canonical.dbusmenu", in_signature="i", out_signature="i")
        def GetParent(self, item_id):
            return 0

    menu_obj = MenuObject(bus, menu_path)

    item_props = {
        "org.kde.StatusNotifierItem": {
            "Category": dbus.String("ApplicationStatus", variant_level=1),
            "Id": dbus.String(ITEM_ID, variant_level=1),
            "Title": dbus.String(args.title, variant_level=1),
            "Status": dbus.String("Active", variant_level=1),
            "WindowId": dbus.UInt32(0, variant_level=1),
            "IconThemePath": dbus.String("", variant_level=1),
            "IconName": dbus.String("phonesbridge", variant_level=1),
            "ItemIsMenu": dbus.Boolean(True, variant_level=1),
            "Menu": dbus.String(menu_path, variant_level=1),
            "IconPixmap": None,  # filled after construction
        }
    }

    class SniObject(PropsObject):
        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="ii", out_signature="")
        def Activate(self, x, y):
            show_menu(x, y)

        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="ii", out_signature="")
        def SecondaryActivate(self, x, y):
            show_menu(x, y)

        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="ii", out_signature="")
        def ContextMenu(self, x, y):
            show_menu(x, y)

        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="is", out_signature="")
        def Scroll(self, delta, orientation):
            pass

    if pixmap:
        item_props["org.kde.StatusNotifierItem"]["IconPixmap"] = dbus.Array(
            [(dbus.Int32(p[0]), dbus.Int32(p[1]), dbus.ByteArray(p[2])) for p in pixmap],
            signature="(iiay)", variant_level=1)

    sni_obj = SniObject(bus, item_path, item_props)

    def check_server():
        if not server_alive():
            quit_app()
            return False
        return True

    GLib.timeout_add(3000, check_server)

    try:
        watcher = bus.get_object("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher")
        watcher.RegisterStatusNotifierItem(bus.get_unique_name(),
                                           dbus_interface="org.kde.StatusNotifierWatcher")
    except Exception as e:
        sys.stderr.write("tray: failed to register with StatusNotifierWatcher (%s)\n" % e)
        return False

    if gtk_ok:
        _Gtk.main()
    else:
        loop.run()
    return True


# ── Fallback: GTK AppIndicator ───────────────────────────────────────────────
def run_gtk():
    try:
        import gi
        gi.require_version("Gtk", "3.0")
        from gi.repository import Gtk, GLib
    except Exception as e:
        sys.stderr.write("tray: GTK3 unavailable (%s)\n" % e)
        return False

    def resolve_icon_name():
        try:
            Gtk.IconTheme.get_default().load_icon("phonesbridge", 22, 0)
            return "phonesbridge"
        except Exception:
            pass
        return args.icon or "network-wired"

    fallback_name = resolve_icon_name()
    indicator = None
    try:
        gi.require_version("AppIndicator3", "0.1")
        from gi.repository import AppIndicator3 as appindicator
        indicator = appindicator.Indicator.new(
            "com.phonesbridge.tray", fallback_name, appindicator.IndicatorCategory.APPLICATION_STATUS)
        indicator.set_status(appindicator.IndicatorStatus.ACTIVE)
        indicator.set_icon(fallback_name)
    except Exception:
        indicator = None

    menu = Gtk.Menu()
    item_open = Gtk.MenuItem(label="🌐 Open Server (%s)" % URL)
    item_open.connect("activate", lambda *a: shell_open())
    menu.append(item_open)
    menu.append(Gtk.SeparatorMenuItem())
    item_stop = Gtk.MenuItem(label="🛑 Stop the server")
    item_stop.connect("activate", lambda *a: (stop_server(), Gtk.main_quit()))
    menu.append(item_stop)
    menu.show_all()

    if indicator is not None:
        indicator.set_menu(menu)
        indicator.set_title(args.title)
    else:
        status_icon = Gtk.StatusIcon()
        status_icon.set_from_icon_name(fallback_name)
        status_icon.set_tooltip_text("%s server running" % args.title)
        status_icon.connect("activate", lambda icon: menu.popup(None, None, None, None, 1, Gtk.get_current_event_time()))
        status_icon.connect("popup-menu", lambda icon, button, time: menu.popup(None, None, None, None, button, time))

    def check_server():
        if not server_alive():
            Gtk.main_quit()
            return False
        return True

    GLib.timeout_add(3000, check_server)
    Gtk.main()
    return True


if __name__ == "__main__":
    if run_sni():
        sys.exit(0)
    sys.exit(0 if run_gtk() else 1)

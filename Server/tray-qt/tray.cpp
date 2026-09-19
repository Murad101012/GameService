// PhonesBridge system tray icon — Qt6 edition.
//
// Uses QSystemTrayIcon, which is Qt's reference StatusNotifierItem
// implementation: on KDE Plasma (X11 and Wayland) the icon and its QMenu are
// exported over D-Bus and rendered natively by plasmashell's own
// DBusMenuImporter. No hand-rolled D-Bus, no GTK popups, no grabs.
//
// CLI (same contract the old tray.py used):
//   tray --pid <server-pid> --port <port> --title <t> --icon <path> --root <dir>
//
// Menu:
//   Open Server                 -> opens the Web UI in the default browser
//   Restart the server          -> dotnet build first; on success SIGTERM the
//                                  old server and launch the new DLL, then quit
//                                  (the new server spawns its own tray)
//   Stop the server             -> SIGTERM the server, then quit
//
// The tray quits automatically when the server process dies (watchdog).

#include <QAction>
#include <QApplication>
#include <QCommandLineParser>
#include <QDesktopServices>
#include <QDir>
#include <QFileInfo>
#include <QIcon>
#include <QMenu>
#include <QProcess>
#include <QSystemTrayIcon>
#include <QTimer>
#include <QUrl>

#include <signal.h>
#include <unistd.h>

namespace {

int g_serverPid = -1;
int g_port = 8052;
QString g_root;

QString serverUrl()
{
    return QStringLiteral("http://127.0.0.1:%1").arg(g_port);
}

bool serverAlive()
{
    if (g_serverPid <= 0)
        return false;
    if (::kill(g_serverPid, 0) == 0)
        return true;
    return errno != ESRCH;
}

void openServer()
{
    QDesktopServices::openUrl(QUrl(serverUrl()));
}

void stopServer()
{
    if (g_serverPid > 0)
        ::kill(g_serverPid, SIGTERM);
}

// Build latest code, then restart on the new build. Runs detached so it
// survives this tray process exiting: the old server keeps serving during
// the build and is only replaced on build success.
void restartServer()
{
    const QString script = QStringLiteral(
        "LOG=${PHONESBRIDGE_LOG:-/tmp/phonesbridge.log}\n"
        "RLOG=/tmp/phonesbridge-restart.log\n"
        "{\n"
        "echo \"[$(date '+%%F %%T')] Restart requested - building...\"\n"
        "cd %1 || exit 1\n"
        "export ASPNETCORE_ENVIRONMENT=Production\n"
        "if ! dotnet build PhonesBridge.csproj --nologo >>\"$RLOG\" 2>&1; then\n"
        "echo \"[$(date '+%%F %%T')] BUILD FAILED - keeping old server running\"\n"
        "exit 1\n"
        "fi\n"
        "echo \"[$(date '+%%F %%T')] Build OK - stopping old server %2\"\n"
        "kill -TERM %2 2>/dev/null || true\n"
        "for i in $(seq 1 20); do kill -0 %2 2>/dev/null || break; sleep 0.5; done\n"
        "kill -KILL %2 2>/dev/null || true\n"
        "echo \"[$(date '+%%F %%T')] Starting new server\" >>\"$LOG\"\n"
        "cd %1 && exec nohup setsid /usr/share/dotnet/dotnet "
        "\"bin/Debug/net10.0/PhonesBridge.dll\" >>\"$LOG\" 2>&1 < /dev/null\n"
        "} >>/tmp/phonesbridge-restart.log 2>&1\n")
                               .arg(g_root)
                               .arg(g_serverPid);
    QProcess::startDetached(QStringLiteral("/bin/bash"), {QStringLiteral("-c"), script});
}

} // namespace

int main(int argc, char *argv[])
{
    QApplication app(argc, argv);
    QApplication::setQuitOnLastWindowClosed(false);

    QCommandLineParser parser;
    parser.addOption({QStringLiteral("pid"), QStringLiteral("Server PID to manage."), QStringLiteral("pid")});
    parser.addOption({QStringLiteral("port"), QStringLiteral("Server port."), QStringLiteral("port"), QStringLiteral("8052")});
    parser.addOption({QStringLiteral("title"), QStringLiteral("Tray title."), QStringLiteral("title"), QStringLiteral("PhonesBridge")});
    parser.addOption({QStringLiteral("icon"), QStringLiteral("Icon file."), QStringLiteral("icon")});
    parser.addOption({QStringLiteral("root"), QStringLiteral("Server project dir."), QStringLiteral("root")});
    parser.process(app);

    g_serverPid = parser.value(QStringLiteral("pid")).toInt();
    g_port = parser.value(QStringLiteral("port")).toInt();
    g_root = parser.value(QStringLiteral("root"));
    if (g_root.isEmpty())
        g_root = QFileInfo(QString::fromLocal8Bit(argv[0])).absoluteDir().absolutePath();
    const QString title = parser.value(QStringLiteral("title"));
    const QString iconPath = parser.value(QStringLiteral("icon"));

    if (!QSystemTrayIcon::isSystemTrayAvailable())
        return 1;

    QIcon icon(iconPath);
    if (icon.isNull())
        icon = QIcon::fromTheme(QStringLiteral("network-wired"));

    auto *menu = new QMenu();
    QAction *openAction = menu->addAction(QStringLiteral("\U0001F310 Open Server (%1)").arg(serverUrl()));
    QObject::connect(openAction, &QAction::triggered, &openServer);
    menu->addSeparator();
    QAction *restartAction = menu->addAction(QStringLiteral("\U0001F504 Restart the server (build + run)"));
    QObject::connect(restartAction, &QAction::triggered, [] {
        restartServer();
        QApplication::quit();
    });
    QAction *stopAction = menu->addAction(QStringLiteral("\U0001F6D1 Stop the server"));
    QObject::connect(stopAction, &QAction::triggered, [] {
        stopServer();
        QApplication::quit();
    });

    auto *tray = new QSystemTrayIcon(icon);
    tray->setToolTip(QStringLiteral("%1 server running").arg(title));
    tray->setContextMenu(menu);
    QObject::connect(tray, &QSystemTrayIcon::activated, [](QSystemTrayIcon::ActivationReason reason) {
        if (reason == QSystemTrayIcon::Trigger)
            openServer();
    });
    tray->show();

    // Watchdog: quit when the server process dies so no stale icon lingers.
    auto *watchdog = new QTimer();
    QObject::connect(watchdog, &QTimer::timeout, [] {
        if (!serverAlive())
            QApplication::quit();
    });
    watchdog->start(3000);

    return QApplication::exec();
}

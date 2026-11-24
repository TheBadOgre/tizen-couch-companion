// UI texts. Polish when Windows' display language is Polish, English otherwise.

using System.Globalization;

static class S
{
    public static readonly bool Pl = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl";

    static string T(string en, string pl) { return Pl ? pl : en; }

    public static string AppTitle { get { return "Couch Companion"; } }
    public static string Subtitle { get { return T("Your Samsung TV turns on and off together with your PC.", "Telewizor Samsung włącza się i wyłącza razem z komputerem."); } }

    // status
    public static string GroupStatus { get { return T("Status", "Stan"); } }
    public static string StInstalled { get { return T("Installed", "Zainstalowano"); } }
    public static string StNotInstalled { get { return T("Not installed", "Nie zainstalowano"); } }
    public static string StTasks { get { return T("Scheduled tasks", "Zadania harmonogramu"); } }
    public static string StShutdown { get { return T("Shutdown script", "Skrypt wyłączania"); } }
    public static string StHelperRunning { get { return T("Background helper", "Komponent w tle"); } }
    public static string StTv { get { return T("TV", "Telewizor"); } }
    public static string TvOffline { get { return T("unreachable", "nieosiągalny"); } }
    public static string Yes { get { return T("yes", "tak"); } }
    public static string No { get { return T("no", "nie"); } }
    public static string Unknown { get { return T("unknown", "nieznany"); } }
    public static string TvUnreachable { get { return T("not reachable (off or other address)", "nieosiągalny (wyłączony lub inny adres)"); } }
    public static string Refresh { get { return T("Refresh", "Odśwież"); } }

    // TV
    public static string GroupTv { get { return T("TV", "Telewizor"); } }
    public static string TvAddress { get { return T("IP address", "Adres IP"); } }
    public static string TvMac { get { return T("MAC address", "Adres MAC"); } }
    public static string Find { get { return T("Find TVs", "Znajdź"); } }
    public static string Finding { get { return T("Looking for Samsung TVs on the network...", "Szukam telewizorów Samsung w sieci..."); } }
    public static string FoundNone { get { return T("No Samsung TV found. Check that it is ON and on the same network.", "Nie znaleziono telewizora Samsung. Sprawdź, czy jest WŁĄCZONY i w tej samej sieci."); } }
    public static string FoundTvs { get { return T("Found TVs", "Znalezione"); } }
    public static string TvPrereq { get { return T("Required TV settings...", "Wymagane ustawienia TV..."); } }
    public static string TvPrereqText
    {
        get
        {
            return T(
                "On the TV (names may differ slightly between firmware versions):\n\n" +
                "  - Settings > General > Network > Expert Settings > Power On with Mobile = On\n" +
                "  - Settings > General > Network > Expert Settings > IP Remote = On\n" +
                "  - Settings > General > External Device Manager > Device Connection Manager >\n" +
                "    Access Notification = First Time Only\n\n" +
                "The TV must be ON and on the same network as this PC (cable is more reliable than Wi-Fi).\n" +
                "During installation accept the prompts on the TV (\"Allow\").\n" +
                "Recommended: reserve the TV's IP address in your router (DHCP reservation).",
                "Na telewizorze (nazwy mogą się nieco różnić zależnie od wersji oprogramowania):\n\n" +
                "  - Ustawienia > Ogólne > Sieć > Ustawienia eksperckie > Włączanie z urządzeniem mobilnym = Wł.\n" +
                "  - Ustawienia > Ogólne > Sieć > Ustawienia eksperckie > IP Remote = Wł.\n" +
                "  - Ustawienia > Ogólne > Menedżer urządzeń zewnętrznych > Menedżer połączeń urządzeń >\n" +
                "    Powiadomienie o dostępie = Tylko za pierwszym razem\n\n" +
                "Telewizor musi być WŁĄCZONY i w tej samej sieci co komputer (kabel jest pewniejszy niż Wi-Fi).\n" +
                "Podczas instalacji zaakceptuj komunikaty na telewizorze („Zezwól”).\n" +
                "Zalecane: zarezerwuj adres IP telewizora w routerze (rezerwacja DHCP).");
        }
    }

    // settings
    public static string GroupSettings { get { return T("Settings", "Ustawienia"); } }
    public static string PcInput { get { return T("TV input of this PC", "Wejście TV tego komputera"); } }
    public static string PcInputNone { get { return T("(never switch)", "(nie przełączaj)"); } }
    public static string PcInputHint { get { return T("On wake-up the TV is switched to this input; it is switched off on sleep only while it shows this input.", "Po wybudzeniu telewizor przełącza się na to wejście; przy uśpieniu gaśnie tylko, gdy pokazuje to wejście."); } }
    public static string KeepOnRestart { get { return T("Keep the TV on when Windows restarts", "Nie wyłączaj TV przy ponownym uruchamianiu Windows"); } }
    public static string WakeOnInput { get { return T("Turn the TV on when the mouse or keyboard is used", "Włączaj TV po ruchu myszy lub klawiaturze"); } }
    public static string GraceSeconds { get { return T("...only if the TV has been off for at least (s)", "...tylko gdy TV jest wyłączony co najmniej (s)"); } }
    public static string KeepOff { get { return T("Switch the TV off again if it turns itself back on after the remote", "Wyłączaj ponownie TV, który sam się włączył po wyłączeniu pilotem"); } }
    public static string KeepOffHint { get { return T("Game controllers do not count as using the PC; leave this off if you turn the TV on with the remote soon after switching it off.", "Pad nie liczy się jako używanie komputera; zostaw wyłączone, jeśli włączasz TV pilotem krótko po wyłączeniu."); } }
    public static string PowerTest { get { return T("Test during installation (TV turns off and on, ~1 min)", "Test podczas instalacji (TV wyłączy się i włączy, ~1 min)"); } }

    // actions
    public static string Install { get { return T("Install", "Instaluj"); } }
    public static string Update { get { return T("Update", "Aktualizuj"); } }
    public static string Uninstall { get { return T("Uninstall", "Odinstaluj"); } }
    public static string OpenLogs { get { return T("Open log folders", "Otwórz foldery logów"); } }
    public static string Details { get { return T("Details", "Szczegóły"); } }
    public static string ShowDetails { get { return T("Show details", "Pokaż szczegóły"); } }
    public static string CopyOutput { get { return T("Copy output", "Kopiuj wynik"); } }
    public static string Close { get { return T("Close", "Zamknij"); } }
    public static string GroupOutput { get { return T("Progress", "Przebieg"); } }

    // messages
    public static string Ready { get { return T("Ready.", "Gotowe."); } }
    public static string Working { get { return T("Working... do not use the TV remote.", "Trwa... nie używaj pilota TV."); } }
    public static string AcceptOnTv { get { return T("Accept the prompt on the TV (\"Allow\").", "Zaakceptuj komunikat na telewizorze („Zezwól”)."); } }
    public static string InstallOk { get { return T("Installation completed.", "Instalacja zakończona."); } }
    public static string InstallWarn { get { return T("Installed, but with problems - see the output.", "Zainstalowano, ale z problemami - zobacz przebieg."); } }
    public static string InstallFailed { get { return T("Installation failed - see the output.", "Instalacja nie powiodła się - zobacz przebieg."); } }
    public static string UninstallConfirm { get { return T("Remove Couch Companion from this PC?\n\nScheduled tasks, the shutdown script and the installed files are removed.", "Usunąć Couch Companion z tego komputera?\n\nZostaną usunięte zadania harmonogramu, skrypt wyłączania i zainstalowane pliki."); } }
    public static string UninstallOk { get { return T("Couch Companion was removed.\n\nOptional: remove this PC from the TV's device list.", "Couch Companion został usunięty.\n\nOpcjonalnie: usuń ten komputer z listy urządzeń w telewizorze."); } }
    public static string UninstallFailed { get { return T("Uninstall failed - see the output.", "Odinstalowanie nie powiodło się - zobacz przebieg."); } }
    public static string BusyClose { get { return T("An operation is still running. Close anyway?", "Operacja jest w toku. Zamknąć mimo to?"); } }
    public static string ErrIp { get { return T("Enter a valid IPv4 address, or use \"Find TVs\".", "Podaj poprawny adres IPv4 albo użyj „Znajdź”."); } }
    public static string ErrGrace { get { return T("Enter 5-600 seconds.", "Podaj 5-600 sekund."); } }
    public static string ErrSources { get { return T("Program files not found (src folder). Run Setup.cmd from the Couch Companion folder.", "Nie znaleziono plików programu (folder src). Uruchom Setup.cmd z folderu Couch Companion."); } }
    public static string NotAdmin { get { return T("This program needs administrator rights.", "Ten program wymaga uprawnień administratora."); } }
    public static string Error { get { return T("Error", "Błąd"); } }
}

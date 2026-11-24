# Couch Companion

**Telewizor Samsung (Tizen) podąża za komputerem z Windows** - dla komputera używanego jako konsola
w salonie (Steam Big Picture, granie z kanapy). Karty graficzne nie mają HDMI-CEC; Couch Companion
robi to przez sieć domową. Bez chmury, bez konta, bez dodatkowego sprzętu.

[English](README.md)

| Komputer... | Telewizor... |
|---|---|
| zasypia (także ze Steam) | **gaśnie** - tylko gdy pokazuje komputer |
| zostaje wybudzony przez Ciebie | **włącza się** i **przełącza na wejście komputera** (także z dekodera lub aplikacji TV) |
| budzi się sam (aktualizacje Windows) | zostaje bez zmian |
| wyłącza się / uruchamia ponownie | gaśnie / zostaje włączony (do wyboru) |

Opcjonalnie: włączanie telewizora myszą lub klawiaturą; ponowne wyłączanie telewizora, który sam
włączył się po wyłączeniu pilotem.

## Instalacja

1. Na telewizorze: *Ustawienia > Ogólne > Sieć > Ustawienia eksperckie* - włącz **Włączanie z
   urządzeniem mobilnym** i **IP Remote**. Ta sama sieć co komputer; najlepiej kabel.
2. Kliknij dwukrotnie **`Setup.cmd`** - zbuduje aplikację instalacyjną kompilatorem wbudowanym
   w Windows i ją uruchomi (wymaga uprawnień administratora).
3. **Znajdź** telewizor, wybierz wejście komputera, **Instaluj** i zaakceptuj komunikaty na telewizorze.

Ta sama aplikacja aktualizuje i odinstalowuje. Po polsku lub angielsku, według języka Windows.
Wymaga Windows 10/11 i telewizora Samsung z Tizen (2016+).

## Jak to działa

IP Control (HTTPS, port 1516) do jawnego włączania/wyłączania i przełączania wejścia, kanał pilota
(port 8002) i Wake-on-LAN jako zapas, oraz ~2 MB komponent w tle, który gasi telewizor w chwili, gdy
Windows zapowiada uśpienie. Szczegóły (po angielsku): [docs/how-it-works.md](docs/how-it-works.md).

Logi: `C:\ProgramData\CouchCompanion` i `%LOCALAPPDATA%\CouchCompanion`.

## Autorzy

**Couch Companion** - **TheBadOgre**, na bazie
[SamsungTV-Companion](https://github.com/leovicyto/SamsungTV-Companion) autorstwa **leovicyto**.
Licencja MIT.

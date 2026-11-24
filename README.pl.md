# Couch Companion

*Vibecoded with love* :heart:

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

## Obsługiwane telewizory

Windows 10/11 i telewizor Samsung z Tizen. Możliwości zależą od tego, które interfejsy sieciowe ma dany model:

| Rocznik TV | Tizen | Kanał pilota (wss :8002) | IP Control (https :1516) | Efekt |
|---|---|---|---|---|
| 2023+ | 7.0+ | tak | tak | **wszystko** - testowane na QE55Q80C (2023, Tizen 7.0) |
| 2020-2022 | 5.5-6.5 | tak | tak | wszystko (2020 / Tizen 5.5 testował oryginalny projekt) |
| 2018-2019 | 4.0-5.0 | tak | nie (port 1515, nieużywany) | tylko włącz/wyłącz: bez przełączania wejścia, przy uśpieniu TV gaśnie niezależnie od tego, co pokazuje |
| 2016-2017 | 2.4-3.0 | nie (tylko 8001) | nie | nieobsługiwane |

IP Control wymaga włączonego *IP Remote* w telewizorze. Niektóre modele nie mają jego części (np.
przełączania wejścia w The Frame 2024); telewizor nadal podąża za komputerem, tylko wejście się nie zmienia.

## Jak to działa

IP Control (HTTPS, port 1516) do jawnego włączania/wyłączania i przełączania wejścia, kanał pilota
(port 8002) i Wake-on-LAN jako zapas, oraz ~2 MB komponent w tle, który gasi telewizor w chwili, gdy
Windows zapowiada uśpienie. Szczegóły (po angielsku): [docs/how-it-works.md](docs/how-it-works.md).

Logi: `C:\ProgramData\CouchCompanion` i `%LOCALAPPDATA%\CouchCompanion`.

## Autorzy

**Couch Companion** - **TheBadOgre**, vibecoded with love (z pomocą AI).
Na bazie [SamsungTV-Companion](https://github.com/leovicyto/SamsungTV-Companion) autorstwa **leovicyto**.
Licencja MIT.

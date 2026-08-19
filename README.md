# SharpView

**GPU-akcelerisani preglednik slika za Windows — Direct3D 12 + .NET 10 (čisti Win32 host, bez UI frameworka)**

## O projektu

Ovo je kompletno refaktorisana i iznova napisana verzija starog projekta koji sam
napravio prije nekoliko godina. Ideja je od početka bila ista: brz, minimalan
preglednik bez ijedne suvišne funkcije, izgrađen direktno na DirectX-u - još je
originalna verzija koristila Direct3D 12, upravo zbog najboljih mogućih
performansi. Ta osnova je zadržana; iz temelja je redizajnirano *kako* se ona
koristi: upravljanje GPU resursima bez ijednog CPU-GPU stall-a, render petlja
koja radi samo kad ima šta da se crta, i dekodiranje prebačeno na WIC.

Vodeći princip: otvaranje slike i listanje foldera treba da budu ograničeni
brzinom diska i kodeka - ne arhitekturom aplikacije. Sve odluke ispod su
podređene tome.

## Brzi start

Potrebno: Windows 10/11 (x64), .NET 10 SDK, GPU sa D3D12 podrškom
(feature level 11_0 — rade i stariji Kepler/Haswell/GCN 1.0; bez ijednog
hardverskog D3D12 adaptera aplikacija pada na WARP softverski rasterizer, spor
ali funkcionalan). Zavisnosti su Vortice.Windows paketi (Direct3D12, DXGI,
D3DCompiler, DirectComposition, Direct2D1 - u njemu žive WIC omotači), `System.Drawing.Common`
(GDI+ fallback dekoder) i `Sdcb.LibRaw.runtime.win64` (LibRaw native binarke
za RAW formate — `raw_r.dll` i prateće dll-ove build sam kopira pored exe-a),
sve se povlači sa NuGet-a. Build očekuje `app.ico` i `app.manifest` pored
`SharpView.csproj`.

    dotnet build -c Release
    dotnet run --project src/SharpView -c Release -- put/do/slike.jpg

Bez argumenta se otvara dijalog za izbor slike. Registracija u Explorerov
"Open with" meni (HKCU, bez administratorskih prava):

    SharpView.exe --register      # asocijacije + ikonica tipa fajla
    SharpView.exe --unregister

Napomena: registracija pamti apsolutnu putanju exe-a - poslije premještanja
aplikacije ponovo pokrenuti.

### Native AOT publish

Distribuciona binarka se pravi Native AOT kompajliranjem — jedan nativni exe,
bez JIT-a i bez potrebe za instaliranim .NET runtime-om, sa trenutnim startom:

    dotnet publish src/SharpView -c Release -r win-x64

Izlaz: `src/SharpView/bin/Release/net10.0-windows/win-x64/publish/` —
`SharpView.exe` plus LibRaw native dll-ovi. Za publish je potreban MSVC linker
(Visual Studio ili Build Tools sa **"Desktop development with C++"**
workload-om); obična `dotnet build` / F5 petlja i dalje radi na CoreCLR-u i ne
traži ništa od toga — AOT se dešava isključivo pri publish-u. `app.manifest`
(PerMonitorV2 DPI), ikonica i version info se ugrađuju i u AOT exe. Prvi
publish je spor (kompajlira se i runtime); naredni su brži.

## Kontrole

| Ulaz | Radnja |
|------|--------|
| ← / → | prethodna / sljedeća slika |
| Home / End | prva / posljednja u folderu |
| 0 | uklopi u prozor (fit) |
| 1 ili dugme "1:1" | tačno 100 % (1 texel = 1 piksel ekrana) |
| + / − | zum |
| točkić miša | zum ka kursoru |
| lijevi klik + povlačenje | pomjeranje slike |
| dupli klik | 1:1 ↔ fit |
| klik na thumbnail | skok na tu sliku |
| hover uz gornju ivicu | naslovna traka: povlačenje prozora, X, desni klik = sistemski meni |
| hover uz donju ivicu | traka thumbnailova (pojavi se i sama pri navigaciji, ~3 s) |
| povlačenje ivice / ćoška | promjena veličine prozora |
| Esc | izlaz |

Pri startu je prozor maksimizovan i potpuno bez klasičnog okvira — slika je
prostrta preko cijele površine (na Windows 11 sa nativnim zaobljenim uglovima
i DWM sjenkom), a sav UI su dva poluprovidna overlaya iste nijanse koja se
sama sklanjaju: naslovna traka na vrhu i traka thumbnailova na dnu. Obje se
pojave čim miš kroči na njihovu površinu (i kad prozor nije fokusiran),
identičnom fade animacijom; traka thumbnailova se pokaže i sama pri navigaciji
strelicama i stoji ~3 sekunde. Slika se prikazuje **1:1 ako u cijelosti staje
u prozor, inače fit** — male slike se nikad ne razvlače nasilno. Ista politika
važi i pri listanju. Prozor se pojavljuje odmah: prva slika se dekodira
asinhrono, paralelno sa inicijalizacijom GPU-a, i iskače čim je gotova — bitno
kod fajlova od više stotina megabajta.

## Arhitektura

    SharpView/
    ├── src/SharpView/
    │   ├── Core/        DeviceResources (uređaj, swap chain, PSO, fence, deferred release),
    │   │                Shaders, TextureUploader, Vertex, ViewConstants
    │   ├── Rendering/   ImageRenderer (glavna slika + prefetch), ThumbnailStrip, TopBar,
    │   │                ZoomPanController
    │   ├── Services/    ImageDecoder (RAW + WIC + GDI+ fallback), WicDecoder, RawDecoder,
    │   │                LibRawNative, TiffOrientation, JpegOrientation, PixelOrientation,
    │   │                ImageNavigator, ThumbnailCache
    │   ├── Platform/    FileAssociations (HKCU registry), WindowStyling (DWM stilizacija),
    │   │                NativeMethods (Win32 interop: prozor, poruke, dijalozi)
    │   └── ViewerApp.cs / ViewerWindow.cs / Program.cs / app.manifest
    └── tests/SharpView.Tests/    unit testovi (ZoomPanController, ImageNavigator,
                                  TiffOrientation, JpegOrientation, PixelOrientation)

Cijeli prikaz je jedan shader par i jedan quad: glavna slika, thumbnailovi i
UI pravougaonici (pozadine traka, okvir selekcije) crtaju se istim
pipeline-om: `TintColor.a` bira teksturni ili solid mod, a `Misc.x` je
zajednički opacity množilac kojim obje trake blijede kao jedna ploha
(teksture i solid boje zajedno). `ZoomPanController`
je čista matematika bez GPU/UI zavisnosti, pa je u potpunosti pokriven unit
testovima.

Ljuska prozora je čisti Win32: `ViewerWindow` registruje window klasu i vodi
vlastiti WndProc preko `UnmanagedCallersOnly` function pointera, a render
petlja sama prazni message queue (`PeekMessage`) umjesto WinForms
`DoEvents`-a. Nema UI frameworka između aplikacije i sistema — dijalozi su
`MessageBoxW` i `GetOpenFileNameW`, DPI (PerMonitorV2) dolazi iz
`app.manifest`-a. Cijeli interop (i Win32 i LibRaw) je `[LibraryImport]` bez
runtime marshalinga, pa je ljuska spremna za Native AOT.

Prozor se ponaša kao pravi sistemski uprkos tome što okvira nema:
`WS_THICKFRAME` (vidljivi okvir uklonjen u `WM_NCCALCSIZE`) donosi resize
ivicama/ćoškovima sa nativnim kursorima, Snap Layouts na povlačenje ka vrhu,
bočni snap i Win+strelice; drag-restore iz maksimizovanog stanja je
implementiran ručno (borderless prozoru ga sistem ne daje). Interaktivni
resize mišem je *frozen-geometry* gest: OS prozor se jednom parkira na
maksimalni doseg gesta i više se ne mijenja do puštanja dugmeta — po pokretu
miša mijenja se samo sadržaj (offset + prividna veličina u prevelikom baferu,
prozor sam clip-uje), čime se DWM-ova dva asinhrona kanala (geometrija,
sadržaj) svode na jedan i resize je bez treptaja po konstrukciji, jednako
gladak za lijevu i desnu ivicu. Swap chain baferi su *sticky-max* (samo rastu),
pa tranzicije gesta i maximize/restore nikad ne pogode `ResizeBuffers` procjep.

Tok jednog frejma: `Update` (animacije + konstante) → `BeginFrame` → uploadi
tekstura snimljeni u frejmovu command listu → draw glavne slike → draw stripa →
`EndFrame` (Present + fence signal). Sva dekodiranja se dešavaju na thread
poolu i nikad ne dodiruju render thread.

## Zašto je brzo

Šest odluka nosi praktično sav efekat; ostalo je higijena.

**1. Nula CPU–GPU stall-ova u toku rada.** Klasična zamka D3D12 aplikacija je
`WaitForGpu()` poslije svakog upload-a teksture. Ovdje se uploadi (glavna slika
i thumbnailovi) snimaju direktno u frejmovu command listu — redoslijed
izvršavanja na istoj queue garantuje da je kopija gotova prije crtanja — a
staging baferi, stare teksture i SRV slotovi oslobađaju se preko *fence-tagged
deferred* mehanizma: svaki resurs nosi fence vrijednost i pušta se tek kad je
GPU prođe. CPU čeka GPU jedino pri resize-u i gašenju.

**2. Render na zahtjev.** Petlja crta samo dok se nešto dešava (animacija
zuma/skrola, prevlačenje, decode ili upload u toku); kad se sve smiri,
aplikacija spava uz ~4 ms poll. Statična slika na ekranu znači približno 0 %
CPU-a i GPU-a, umjesto punog jezgra potrošenog na crtanje identičnog frejma na
svakom vsync-u.

**3. WIC dekodiranje sa skaliranim thumbovima.** WIC je višestruko brži od
GDI+, a ključni detalj za strip: skaler zakačen direktno na frame pušta JPEG
kodeku da dekodira nativno na umanjenu rezoluciju (DCT skaliranje) — 50 MP
fotografija se nikad ne dekodira cijela da bi se dobio thumbnail od 80 px.
GDI+ ostaje kao automatski runtime fallback, pa ponašanje može samo da se
popravi, nikad da regresira.

**4. BGRA od kraja do kraja.** I GDI+ i WIC nativno daju 32-bitni BGRA;
teksture su `B8G8R8A8_UNorm`, pa je put od dekodera do GPU-a čist `memcpy`
(jedan blok kad je stride tijesan) — bez per-pixel zamjene kanala koja je
ranije dominirala pripremom velikih slika.

**5. Prefetch susjeda + "promotion".** Dok je na ekranu slika N, u pozadini se
dekodiraju N−1 i N+1 (keš ograničen na 4 slike / 512 MB), pa je pritisak na
strelicu praktično trenutan. Ako zahtjev za navigaciju stigne dok prefetch
iste slike još traje, registruje se *promocija*: gotov prefetch se isporuči
direktno, umjesto da krene drugi decode istog fajla — brzo listanje ne radi
dupli posao.

**6. Bez uzaludnog posla u stripu.** Zahtjevi za thumbnailove nose i "wanted"
set vidljivog opsega: decode job koji je čekao u redu, a čiji je thumbnail u
međuvremenu iskliznuo iz kadra, preskače dekodiranje. LRU keš (120 unosa) radi
O(1) poteze, a evikcija ide kroz isti fence deferred mehanizam — opet bez
stall-a.

Higijena ispod toga: opaque PSO (shader ionako uvijek vraća alfa = 1, pa bi
blending samo trošio ROP bandwidth), bez alokacija po frejmu u vrućim
petljama, `Stopwatch` umjesto `DateTime` tajminga, i **pixel snapping** — kad
se animacija smiri, ugao odredišnog pravougaonika se zaokruži na pikselsku
mrežu, pa je 1:1 bit-perfect (bez half-pixel zamućenja kad je razlika
prozor − slika neparna).

## Podržani formati

PNG, JPEG, BMP, GIF (prvi frejm), TIFF — uvijek. WebP i HEIC/HEIF —
automatski, ako su na mašini instalirane Windows kodek ekstenzije ("WebP Image
Extensions", odnosno "HEIF Image Extensions"; za HEVC-kodiran HEIC dodatno i
HEVC kodek). Detekcija je runtime: ekstenzije se pojavljuju u navigaciji i
dijalogu samo kad stvarno rade, pa nema tihih promašaja.

### RAW (NEF, NRW, DNG, RAF)

RAW formati se dekodiraju kroz bundlovan **LibRaw** (`raw_r.dll`, thread-safe
build iz `Sdcb.LibRaw.runtime.win64` paketa): Nikon NEF/NRW, Adobe/kamera DNG
i Fuji RAF. Politika je *preview-first*: iz RAW-a se izvuče JPEG preview koji
kamera ugrađuje (obično puna rezolucija) i dekodira kroz postojeći WIC put —
čita se samo JPEG blok, senzorski podaci se ne diraju, pa je otvaranje reda
milisekundi umjesto sekundi. Pravi demozaik (LibRaw dcraw pipeline, kamera WB
preko `cam_mul`) radi samo kao fallback kad upotrebljiv preview ne postoji.
U praksi: kamera fajlovi (NEF/NRW/RAF, in-camera DNG) praktično uvijek lete
preview putem; Adobe-konvertovani DNG zna imati samo srednji preview
(~1024 px) koji veličinski gate ispravno odbije — tada ide demozaik, sporiji
ali pošten (kod RAF-a bi bio i osjetno sporiji zbog X-Trans senzora, pa je
preview put tamo još bitniji).

EXIF orijentacija radi za sve RAW formate: NEF/NRW/DNG su TIFF kontejneri,
pa se Orientation tag čita iz IFD0 (vlastiti mini čitač, nezavisan od verzije
LibRaw-a); RAF nije TIFF, pa se tag čita iz EXIF bloka ugrađenog JPEG-a koji
je ionako već u memoriji (`JpegOrientation` nađe APP1/Exif segment i preda ga
istom TIFF čitaču). Demozaik put rotaciju dobija besplatno jer je LibRaw sam
primjenjuje. Napomena o bojama: preview je kamerin JPEG rendering (picture
control, izoštravanje), pa se od "sirovog" demozaika može minimalno
razlikovati — za preglednik je to poželjno, slika izgleda kao na poleđini
aparata.

LibRaw sloj je format-agnostičan (LibRaw fajl prepoznaje po sadržaju, ne po
ekstenziji), pa se dalji formati dodaju jednom ekstenzijom u
`RawDecoder.ExtensionList` + registracijama + testom sa stvarnim fajlovima.
Licenca: native binarke su LGPL-2.1-only OR CDDL-1.0 (dinamičko linkovanje,
kompatibilno sa MIT licencom projekta; zahtijevaju VC++ runtime — instaler ga
rješava preuzimanjem, portable ZIP ga nosi app-local).

## TODO

- [ ] **Mipmape za glavnu sliku** — CPU generisanje mip lanca tokom
  background dekodiranja + upload svih nivoa; uklanja treperenje i moiré na
  fit prikazu velikih fotografija i vraća smisao anizotropnom filtriranju.
  Najveći preostali golim okom vidljiv dobitak.
- [ ] **Formati** — Dodaj nove formate, ukljucujuci .ico i formate za iphone,
  i .raw formate — **RAW gotov: NEF, NRW, DNG, RAF (LibRaw)**; ostaje .ico
  (u navigaciji; otvaranje već radi kroz WIC) i eventualni dalji RAW formati
  (jedna ekstenzija u `RawDecoder` + registracije + test).
- [ ] Windows 10 ne radi associate files u exe setup
- [ ] **EXIF orijentacija** — auto-rotacija fotografija sa telefona; bez nje
  se portretni snimci prikazuju položeni na bok. **Djelimično: za sve RAW
  formate urađeno** (`TiffOrientation` + `JpegOrientation` +
  `PixelOrientation`); ostaje primjena istog mehanizma na JPEG/TIFF put —
  čitači već postoje, treba ih samo pozvati iz WIC/GDI+ grane dekodera.
- [ ] Sortiranje po datumu (i veličini) pored abecednog.
- [ ] Borderless fullscreen (F11) bez naslovne trake.

## Napomena o razvoju

Refaktor je rađen u paru sa AI asistentom: analiza postojećeg koda,
dizajn fence-based upravljanja GPU resursima, WIC dekoder,
prefetch/promotion mehanika, čista Win32 ljuska i frozen-geometry resize,
LibRaw RAW podrška, Native AOT / instaler / CI i dokumentacija.
Sve izmjene su ručno pregledane, kompajlirane i testirane prije uključivanja;
odgovornost za kod je u potpunosti moja.

## Licenca

[MIT LICENSE](LICENSE).

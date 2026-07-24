# SharpView

**GPU-akcelerisani preglednik slika za Windows — Direct3D 12 + .NET 10 (WinForms host)**

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
(feature level 12_0). Zavisnosti su Vortice.Windows paketi (Direct3D12, DXGI,
D3DCompiler, Direct2D1 - u njemu žive WIC omotači), povlače se sa NuGet-a.
Build očekuje `app.ico` pored `SharpView.csproj`.

    dotnet build -c Release
    dotnet run --project src/SharpView -c Release -- put/do/slike.jpg

Bez argumenta se otvara dijalog za izbor slike. Registracija u Explorerov
"Open with" meni (HKCU, bez administratorskih prava):

    SharpView.exe --register      # asocijacije + ikonica tipa fajla
    SharpView.exe --unregister

Napomena: registracija pamti apsolutnu putanju exe-a - poslije premještanja
aplikacije ponovo pokrenuti.

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
| Esc | izlaz |

Pri startu je prozor maksimizovan, naslovna traka tamna (DWM immersive dark
mode + Mica na Windows 11), a slika se prikazuje **1:1 ako u cijelosti staje u
prozor, inače fit** — male slike se nikad ne razvlače nasilno. Ista politika
važi i pri listanju. Prozor se pojavljuje odmah: prva slika se dekodira
asinhrono, paralelno sa inicijalizacijom GPU-a, i iskače čim je gotova — bitno
kod fajlova od više stotina megabajta.

## Arhitektura

    SharpView/
    ├── src/SharpView/
    │   ├── Core/        DeviceResources (uređaj, swap chain, PSO, fence, deferred release),
    │   │                Shaders, TextureUploader, Vertex, ViewConstants
    │   ├── Rendering/   ImageRenderer (glavna slika + prefetch), ThumbnailStrip, ZoomPanController
    │   ├── Services/    ImageDecoder (WIC + GDI+ fallback), WicDecoder, ImageNavigator, ThumbnailCache
    │   ├── Platform/    FileAssociations (HKCU registry), WindowStyling (DWM stilizacija)
    │   └── ViewerApp.cs / ViewerForm.cs / Program.cs
    └── tests/SharpView.Tests/    14 unit testova (ZoomPanController, ImageNavigator)

Cijeli prikaz je jedan shader par i jedan quad: glavna slika, thumbnailovi i
UI pravougaonici (pozadina stripa, okvir selekcije) crtaju se istim
pipeline-om, a `TintColor.a` bira teksturni ili solid mod. `ZoomPanController`
je čista matematika bez GPU/UI zavisnosti, pa je u potpunosti pokriven unit
testovima.

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

## TODO

- [ ] **Mipmape za glavnu sliku** — CPU generisanje mip lanca tokom
  background dekodiranja + upload svih nivoa; uklanja treperenje i moiré na
  fit prikazu velikih fotografija i vraća smisao anizotropnom filtriranju.
  Najveći preostali golim okom vidljiv dobitak.
- [ ] **EXIF orijentacija** — auto-rotacija fotografija sa telefona; bez nje
  se portretni snimci prikazuju položeni na bok.
- [ ] Sortiranje po datumu (i veličini) pored abecednog.
- [ ] Borderless fullscreen (F11) bez naslovne trake.

## Napomena o razvoju

Refaktor je rađen u paru sa AI asistentom: analiza postojećeg koda, 
dizajn fence-based upravljanja GPU resursima, WIC dekoder,
prefetch/promotion mehanika i dokumentacija.
Sve izmjene su ručno pregledane, kompajlirane i testirane prije uključivanja;
odgovornost za kod je u potpunosti moja.

## Licenca

[MIT LICENSE](LICENSE).

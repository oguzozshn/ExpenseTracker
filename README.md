# ExpenseTracker

Kişisel harcama takibi. Blazor Server + EF Core/SQLite, tek kullanıcılı, tamamen yerel:
veri kullanıcı profilinde duran bir `.db` dosyasında kalır, dışarıya hiçbir istek gitmez.

## Çalıştırma

```bash
dotnet run --project ExpenseTracker.csproj --launch-profile http
```

http://localhost:5290 açılır. İlk açılışta veritabanı oluşturulur ve Türkçe bir
varsayılan kategori seti (Market, Kira, Faturalar, … / Maaş, Ek Gelir) yüklenir.

Testler:

```bash
dotnet test Tests/ExpenseTracker.Tests.csproj
```

## Sayfalar

| Sayfa | Ne yapar |
|---|---|
| **Panel** | Dönem özeti (gider/gelir/net/günlük ortalama), hesap bakiyeleri, günlük harcama grafiği, kategori dağılımı, bütçe durumu, yaklaşan düzenli işlemler, son 6 dönem karşılaştırması, hızlı işlem ekleme |
| **İşlemler** | Dönem içi liste; tür, kategori, hesap ve metin filtresi; ekleme, düzenleme, silme; CSV dışa aktarma |
| **Hesaplar** | Nakit / banka / kredi kartı hesapları, açılış bakiyesi, hesap bazlı bakiye ve toplam varlık-borç |
| **Bütçeler** | Kategori bazlı dönemlik sınır, doluluk çubuğu ve "bu hızla dönem sonu" tahmini |
| **Düzenli** | Kira/abonelik/maaş gibi her ay tekrar eden kalemler; vadesi gelenler gerçek işlem olarak yazılır |
| **Kategoriler** | Gider/gelir kategorileri; renk, simge, sıralama, arşivleme |
| **İçe aktar** | Banka ve kredi kartı ekstresini (CSV/Excel) yükleme, önizleme ve tek tıkla geri alma |
| **Ayarlar** | Para birimi simgesi, dönem başlangıç günü, veri özeti ve dosya konumu |

## Ekstre içe aktarma

Bankanın verdiği dosyayı **İçe aktar** sayfasından yükleyin. Akış üç adım: dosya → sütun
eşlemesi → önizleme. Onaylamadan hiçbir şey yazılmaz.

Dosyadan okunanlar otomatik sezilir: ayraç (`;` `,` sekme), kodlama (UTF-8 veya
windows-1254) ve gerçek başlık satırı — bankaların dosya başına koyduğu hesap bilgisi
blokları atlanır. `.xlsx` dosyaları da doğrudan okunur.

**Sütunlar tahmin edilir**, siz düzeltirsiniz. Tek "Tutar" sütunu da, ayrı "Borç/Alacak"
sütunları da desteklenir. Bakiye sütunu asla tutar sanılmaz.

**Kredi kartı ekstrelerinde işaret ters olur** — harcamalar artı yazılır. Kart hesabına
aktarırken bu sezilip işaret otomatik çevrilir; önizlemeye bakarak tek tıkla geri
alabilirsiniz.

**Kategoriler kurallarla atanır.** Kurulumda MİGROS, ŞOK, SHELL, TURKCELL, NETFLIX gibi
yaygın Türk işyerleri için kurallar hazır gelir; eşleşme büyük/küçük harf ve Türkçe
karakter farkını yok sayar. Kategorisi olmayan bir satıra kategori verip **Kural yap**
derseniz hem o ekstredeki benzerlerine uygulanır hem de gelecek ekstreler için saklanır.
Kategorisiz kalan satır aktarılmaz.

**Mükerrer kayıt engellenir.** Her satırın parmak izi `(tarih, tutar, normalleştirilmiş
açıklama, o gün içindeki sıra)` üzerinden hesaplanır ve hesap bazında tekil indekste
tutulur. Bankalar her seferinde son 90 günü verdiği için örtüşen ekstreleri arka arkaya
yüklemek güvenlidir; aynı gün aynı yerde yapılan iki ayrı alışveriş ise iki satır olarak
korunur.

**Geri alma** her zaman mümkün: geçmiş içe aktarmalar listesinden bir partiyi silmek,
o partinin yazdığı bütün işlemleri kaldırır.

## Tasarım kararları

**Para tam sayı olarak saklanır.** `AmountMinor` kuruş cinsinden `long`. SQLite'ın
`decimal` tipi yok; EF Core `decimal`'ı TEXT'e yazar ve bu durumda `SUM` ile
`ORDER BY` yanlış sonuç verir. Tam sayı hem toplamları kuruşu kuruşuna doğru tutar
hem de hesapların veritabanı tarafında kalmasını sağlar.

**Nokta binlik ayracıdır.** `1.500` girdisi 1.500,00 ₺ okunur, 1,50 ₺ değil. Kural:
noktadan sonra tam üç rakam varsa (veya birden fazla nokta varsa) binlik ayracı,
aksi halde ondalık ayracı. Kuruş iki hane olduğu için üç haneli grup binliktir.
İki ayraç birden varsa (`1.234,56`) sağdaki ondalıktır. `Tests/MoneyTests.cs`
bu davranışı kilitler.

**Dönem takvim ayı olmak zorunda değil.** Ayarlardaki başlangıç günü 1 dışında bir
değere çekilirse (maaş günü gibi) bütün raporlar ve bütçeler o döngüye göre hesaplanır.
Tüm ay sınırı hesabı tek bir yerde, `Services/Period.cs` içinde.

**İşlemin türü satırda taşınır.** `Kind` kategoriden kopyalanır. Bir kategoriyi sonradan
gider'den gelir'e çevirmek geçmiş ayları yeniden yazamasın diye.

**Transferler toplamlara girmez.** Kart borcu ödemesi ya da ATM'den para çekmek harcama
değildir; hem banka hem kart ekstresi yüklendiğinde aynı para iki kez sayılırdı. Bu yüzden
işlemde bir `IsTransfer` bayrağı var: bakiyeyi etkiler (para gerçekten yer değiştirir) ama
gelir/gider raporlarının hiçbirine girmez. `Hesaplar arası` kategorisi hem gider hem gelir
tarafında bulunur, çünkü transferin iki ucu iki farklı yöndedir.

**Düzenli işlemler tekrar çalıştırılabilir.** `(RecurringRuleId, OccurredOn)` üzerinde
filtreli tekil indeks var; üretici aynı tarihleri kaç kez işlerse işlesin mükerrer kayıt
oluşmaz. Uygulama bir hafta kapalı kalırsa açılışta aradaki aylar tamamlanır. Tek geçişte
en fazla 120 kayıt üretilir — geçmişe yanlışlıkla atılmış bir başlangıç tarihi defteri
doldurmasın diye.

**Kategori silinmez, arşivlenir.** Kullanımdaki bir kategori için `Sil` düğmesi hiç
görünmez; yabancı anahtar da `Restrict`. Geçmiş toplamların sessizce bozulması bu şekilde
imkânsız.

**CSV dışa aktarma normal bir GET.** Blazor Server sayfası JS interop olmadan tarayıcıya
dosya veremediği için `/export.csv` uç noktası var. Noktalı virgülle ayrılmış ve UTF-8
BOM'lu: Türkçe yerel ayarlı Excel bu dosyayı içe aktarma sihirbazı olmadan açar.

## Veri

```
%LOCALAPPDATA%\ExpenseTracker\expenses.db
```

Binaries'in yanında değil, kullanıcı profilinde. Yeniden derlemek, `bin` klasörünü
silmek veya projeyi taşımak geçmişi etkilemez. Yedek almak için bu tek dosyayı kopyalamak
yeterli.

## Yapı

```
Components/
  Layout/          MainLayout, ReconnectModal
  Pages/           Home, Transactions, Accounts, Budgets, Recurring, Categories,
                   Import, Settings
  Shared/          PeriodNav, DailyChart, CategoryBreakdown, TransactionForm
Data/              AppDbContext, Entities, Migrations
Services/          Money, Period, Settings, Category, Transaction, Budget, Recurring,
                   Account, CategoryRule
                   RecurringBackgroundService (6 saatte bir vadesi gelenleri işler)
  Import/          StatementReader  — ayraç/kodlama/başlık sezme, CSV ve xlsx
                   StatementValues  — banka tarih ve tutar biçimleri
                   StatementText    — Türkçe karakter katlama, kural eşleşmesi
                   ColumnMap        — sütun tahmini
                   StatementImporter— önizleme, tekilleştirme, kaydetme, geri alma
Tests/             Money, Period, düzenli işlem tekrarı, ekstre okuma ve
                   içe aktarma tekilleştirme testleri
```

# Tamir ve Bakim Talep Sistemi

Bu proje, fabrikadan tamir veya bakima gonderilen malzemelerin kaydini tutmak ve durum takibini yapmak icin hazirlandi.

## Ozet

- Giris yapan kullanicilar yeni tamir-bakim talebi acabilir.
- Kayitlar gun bazli tutulur ve arsivden tekrar goruntulenebilir.
- Her talep icin manuel formdaki alanlar sisteme kaydedilir.
- Excel raporu alinabilir.
- `admin` kullanicisi kullanici yonetimi yapabilir.

## Kayit Alanlari

- Tarih
- Form no
- Siparis no
- Malzeme cinsi
- Miktar
- Birim
- Aciklama
- Bolum adi
- Siparis sorumlusu
- Unite muduru
- Ambar sorumlusu
- Onay 1
- Onay 2
- Malzemeyi teslim eden
- Teslim tarihi
- Teslim eden imza / ad soyad
- Tamir-bakim icin teslim alan
- Teslim alan imza / ad soyad
- Durum

## Durumlar

- `Talep Acildi`
- `Gonderildi`
- `Tamirde / Bakimda`
- `Teslim Alindi`
- `Iptal`

## Varsayilan Giris

- Kullanici adi: `admin`
- Parola: `System.01`

## Calistirma

\TamirBakimTalepApp
dotnet run
```

Uygulama varsayilan olarak su adreste calisir:

`http://localhost:5079`

## Veri Dosyalari

Ilk acilista `Data` klasoru olusur:

- `Data\users.json`
- `Data\requests.json`

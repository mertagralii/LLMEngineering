# Nova Yazılım — Teknik Kurallar

## Branch ve Commit Kuralları
Ana dal main'dir ve doğrudan push kapalıdır.
Her iş için feature/ veya fix/ önekiyle ayrı bir dal açılır.
Commit mesajları İngilizce ve emir kipinde yazılır.
Bir pull request en fazla 400 satır değişiklik içermelidir.

## Kod İnceleme
Her pull request en az iki onay almadan birleştirilemez.
İnceleme için hedef süre 24 saattir.
Onaylayanlardan en az biri ilgili servisin sahibi ekipten olmalıdır.
Yazar kendi pull request'ini onaylayamaz.

## Test ve Kalite
Yeni eklenen kodda birim test kapsamı en az yüzde 70 olmalıdır.
Testler kırmızıysa pull request birleştirilemez.
Performans etkisi olan değişikliklerde önce ve sonra ölçüm paylaşılır.

## Dağıtım Süreci
Production dağıtımları salı ve perşembe günleri 10:00-16:00 arasında yapılır.
Cuma günü production dağıtımı yapılmaz.
Her dağıtım öncesi geri alma planı yazılı olarak hazırlanır.
Acil düzeltmeler için nöbetçi mühendisin onayı yeterlidir.

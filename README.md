# سوپر بک‌اند

سوپر بک‌اند یک پروژهٔ پایه برای ساخت Web API با C# و.NET ۱۰ LTS است. برنامه به‌صورت یک واحد اجرا و مستقر می‌شود. هر ماژول مسیرهای API، کد و مدل دادهٔ خودش را دارد. برای شروع یک محصول جدید، زیرساخت هویت و دسترسی را بردارید و APIهای دامنهٔ خودتان را کنار آن اضافه کنید.

## اجرای محلی

با Docker و Docker Compose اجرا کنید.

```bash
docker compose up --build -d
curl --fail --retry 8 --retry-delay 1 --retry-all-errors http://localhost:5080/health/ready
```

سرویس `migrate` پیش از بالا آمدن API، migrationهای ماژول‌های فعال را روی PostgreSQL اجرا می‌کند. بعد از آماده‌شدن برنامه می‌توانید [سند OpenAPI](http://localhost:5080/openapi/v1.json) و [صندوق ایمیل محلی](http://localhost:8025) را باز کنید.

برای اجرای API از طریق SDK و ویرایش کد، فقط پایگاه داده و صندوق ایمیل را با Compose بالا بیاورید. اگر کانتینر API روی پورت `5080` در حال اجراست، ابتدا آن را با `docker compose stop api` متوقف کنید.

```bash
docker compose up -d --wait database mail
dotnet restore --locked-mode
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/SuperBackend.Api --no-launch-profile -- --migrate
dotnet run --project src/SuperBackend.Api
```

### اولین مدیر

پس از اعمال migrationها، از ترمینال مورد اعتماد مدیر بسازید. گذرواژه باید دست‌کم ۱۲ نویسه داشته باشد. فرمان ایجاد مدیر، ایمیل حساب تازه را تأیید می‌کند و اگر از قبل حسابی با آن ایمیل وجود داشته باشد، متوقف می‌شود.

```bash
read -rsp "Administrator password: " Bootstrap__Password
printf '\n'
export Bootstrap__Password
docker compose run --rm --no-deps -e Bootstrap__Password migrate --bootstrap-admin admin@example.test
unset Bootstrap__Password
```

در حالت اجرای API از طریق SDK، خط `docker compose run` را با این فرمان جایگزین کنید.

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/SuperBackend.Api --no-launch-profile -- --bootstrap-admin admin@example.test
```

برای ورود، ایمیل و گذرواژه را به `POST /api/v1/auth/login` بفرستید. پاسخ شامل `accessToken` و `refreshToken` است. در درخواست‌های محافظت‌شده، هدر `Authorization: Bearer <accessToken>` را بفرستید.

## قابلیت‌های آماده

- ماژول هویت با ASP.NET Core Identity. ثبت‌نام اختیاری، تأیید ایمیل، بازیابی و تغییر گذرواژه، احراز هویت دومرحله‌ای با TOTP و کد بازیابی، محدودیت تلاش ورود و لغو همهٔ نشست‌ها.
- پروفایل کاربر در `GET /api/v1/me` و `PUT /api/v1/me`. بدنهٔ ویرایش یک فیلد `displayName` دارد.
- کنترل دسترسی مبتنی بر نقش و مجوز. مدیر از API نقش می‌سازد، مجوز به نقش می‌دهد و نقش به کاربر اختصاص می‌دهد. نتیجهٔ تغییر دسترسی در درخواست بعدی با همان توکن اعمال می‌شود.
- ثبت تغییرات دسترسی. ثبت ممیزی و تغییر نقش یا مجوز در یک تراکنش ذخیره می‌شوند. مسیر `GET /api/v1/admin/audit` خروجی را با پارامترهای `before` و `limit` صفحه‌بندی می‌کند.
- زیرساخت HTTP با احراز هویت پیش‌فرض، محدودیت نرخ درخواست، CORS مبتنی بر فهرست مجاز، اعتبارسنجی ورودی، پاسخ‌های Problem Details، OpenAPI در محیط توسعه و مسیرهای بررسی سلامت.
- ماژول اختیاری یادداشت با CRUD، کنترل مالکیت رکورد، صفحه‌بندی و کنترل هم‌زمانی ویرایش. برای دسترسی به یادداشت کاربر دیگر، API پاسخ `404` می‌دهد.

### مسیرهای حساب کاربری

پیشوند مسیرها `/api/v1/auth` است.

| مسیر | کاربرد |
| --- | --- |
| مسیر `POST /register` | ارسال `email` و `password` و دریافت لینک تأیید از SMTP |
| مسیر `GET /confirmEmail` | بازکردن لینک تأیید دریافتی |
| مسیر `POST /resendConfirmationEmail` | ارسال دوبارهٔ لینک با `email` |
| مسیر `POST /login` | دریافت توکن‌ها با `email`، `password` و در صورت نیاز `twoFactorCode` یا `twoFactorRecoveryCode` |
| مسیر `POST /refresh` | دریافت توکن‌ها با `refreshToken` |
| مسیر `POST /forgotPassword` | درخواست کد بازیابی با `email` |
| مسیر `POST /resetPassword` | ارسال `email`، `resetCode` و `newPassword` |
| مسیر `GET /manage/info` | مشاهدهٔ اطلاعات حساب |
| مسیر `POST /manage/info` | تغییر ایمیل یا گذرواژه با هدر `X-Current-Password` |
| مسیر `POST /manage/2fa` | مدیریت TOTP و کدهای بازیابی با هدر `X-Current-Password` |
| مسیر `POST /logout-all` | لغو توکن‌های دسترسی و تمدید حساب در همهٔ دستگاه‌ها |

توکن دسترسی ۱۰ دقیقه و توکن تمدید ۷ روز اعتبار دارد. Identity از توکن Bearer اختصاصی ASP.NET Core استفاده می‌کند. تمدید توکن، نسخهٔ قبلی توکن تمدید را تا زمان انقضا یا لغو نشست‌ها معتبر نگه می‌دارد. برای مدیریت دومرحله‌ای، ابتدا یک درخواست با بدنهٔ `{}` بفرستید، `sharedKey` را در برنامهٔ احراز هویت ثبت کنید. ایجاد اولیهٔ کلید، نشست قبلی را لغو می‌کند. دوباره وارد شوید و سپس `enable: true` و `twoFactorCode` را بفرستید. [مستندات ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0)

### دادن دسترسی به ماژول یادداشت

با توکن مدیر، مراحل زیر را اجرا کنید. پیشوند همهٔ مسیرها `/api/v1/admin` است.

۱. با `POST /roles` و بدنهٔ `{"name":"Note editor"}` نقش بسازید. پاسخ شناسهٔ نقش را برمی‌گرداند.
۲. از `GET /permissions` مجوزهای ماژول‌های فعال را بگیرید.
۳. با `PUT /roles/{roleId}/permissions/notes.read` و `PUT /roles/{roleId}/permissions/notes.write` مجوزها را اختصاص دهید.
۴. شناسهٔ کاربر را از `GET /users` بگیرید. با `PUT /users/{userId}/roles/{roleId}` نقش را به او بدهید.

حالا کاربر می‌تواند در `POST /api/v1/notes` با بدنهٔ `{"title":"First note","body":"Text"}` یادداشت بسازد. برای ویرایش، مقدار `version` دریافتی را در بدنهٔ `PUT /api/v1/notes/{id}` بفرستید. اگر نسخه عوض شده باشد، API پاسخ `409` می‌دهد. برای برداشتن نقش یا مجوز از همان مسیر و متد `DELETE` استفاده کنید. نقش `Administrator` از طریق فرمان عملیاتی قابل تخصیص است.

## ساخت API برای محصول خودتان

فایل `src/SuperBackend.Api/Program.cs` نقطهٔ اتصال ماژول‌هاست. پروژهٔ `SuperBackend.Platform` قرارداد ثبت مجوز و دسترسی به شناسهٔ کاربر را در اختیار ماژول‌ها می‌گذارد. پروژه‌های هویت و یادداشت به یکدیگر وابستگی ندارند و هرکدام DbContext، schema و تاریخچهٔ migration خود را دارند.

۱. یک پروژه در `src/Modules` بسازید. الگوی ثبت سرویس و endpoint را از `NotesModule.cs` بردارید و پروژه را از API مرجع دهید.
۲. اگر ماژول اختیاری است، ثبت سرویس، ثبت endpoint و اجرای migration را با یک گزینه در `Modules` کنترل کنید.
۳. برای هر عملیات، مجوز را با نامی مانند `orders.read` ثبت کنید و روی endpoint از `RequireAuthorization("orders.read")` استفاده کنید. اگر عملیات به یک رکورد خاص مربوط است، مالکیت یا عضویت کاربر را داخل همان ماژول بررسی کنید.
۴. مدل دادهٔ ماژول را در schema متعلق به خودش نگه دارید. اطلاعات مورد نیاز از ماژول دیگر را از قرارداد همان ماژول بگیرید.
۵. با `Modules__Notes=false` ماژول نمونه را خاموش کنید. API، catalog مجوزها و مرحلهٔ migration از همان گزینه استفاده می‌کنند.
۶. پس از تغییر وابستگی‌ها یا افزودن پروژه، `dotnet restore` را اجرا و فایل‌های `packages.lock.json` را همراه کد ثبت کنید.

برای اعتبارسنجی ورودی، روی DTO از Data Annotations و در ثبت endpoint از `ValidateBody<T>()` استفاده کنید.

این نمونه فرم پایهٔ یک endpoint محافظت‌شده را نشان می‌دهد.

```csharp
services.AddAuthorizationBuilder()
    .AddPermission("orders.read", "Read orders");

app.MapGet("/api/v1/orders", (ClaimsPrincipal user, OrderQueries orders, CancellationToken ct) =>
    orders.ListForUserAsync(user.UserId(), ct))
    .RequireAuthorization("orders.read");
```

برای ساخت migration، ابزار محلی EF را بازیابی و پروژهٔ مقصد را مشخص کنید. این نمونه برای تغییر مدل یادداشت است.

```bash
dotnet tool restore
ASPNETCORE_ENVIRONMENT=Development dotnet ef migrations add UpdateNotes \
  --context NotesData --project src/Modules/SuperBackend.Notes \
  --startup-project src/SuperBackend.Api --output-dir Migrations
```

## تنظیمات استقرار

| متغیر محیطی | کاربرد |
| --- | --- |
| متغیر `ConnectionStrings__Main` | اتصال PostgreSQL از طریق secret محیط استقرار |
| متغیر `AllowedHosts` | نام دامنه‌های مجاز برای API با جداکنندهٔ `;` |
| متغیر `Identity__PublicOrigin` | نشانی HTTPS عمومی API، مانند `https://api.example.com` برای لینک‌های ایمیل |
| متغیر `Identity__AllowRegistration` | فعال‌سازی ثبت‌نام عمومی. مقدار پیش‌فرض خارج از توسعه `false` است |
| متغیر `Modules__Notes` | فعال‌سازی ماژول نمونه. مقدار پیش‌فرض خارج از توسعه `false` است |
| متغیر `Smtp__Host`، `Smtp__Port` و `Smtp__From` | سرور SMTP با STARTTLS. پورت پیش‌فرض `587` است |
| متغیر `Smtp__Username` و `Smtp__Password` | اطلاعات ورود به SMTP در صورت نیاز |
| متغیر `DataProtection__KeyDirectory` | مسیر پایدار و با دسترسی محدود برای کلیدهای Data Protection. replicaهای یک محیط به کلیدهای یکسان نیاز دارند |
| متغیر `Http__KnownProxies__0` | IP پراکسی مورد اعتماد برای خواندن `X-Forwarded-For` و `X-Forwarded-Proto`. برای IPهای دیگر شماره را افزایش دهید |
| متغیر `Http__CorsOrigins__0` | مبدأ دقیق فرانت‌اند، مانند `https://app.example.com`. تنظیم خالی یعنی دسترسی مرورگری از مبدأ دیگر غیرفعال است |

فایل Compose برای توسعهٔ محلی است. در استقرار، TLS را روی میزبان API یا پراکسی خاتمه دهید. درخواست‌های HTTP در محیط تولید با `400` رد می‌شوند. health check را هم از مسیر HTTPS اجرا کنید. مسیر `/health/live` سلامت فرایند را بررسی می‌کند و مسیر `/health/ready` اتصال پایگاه دادهٔ هر ماژول فعال را می‌سنجد.

برای اتصال PostgreSQL روی شبکه، از TLS با `SSL Mode=VerifyFull` و زنجیرهٔ اعتماد مناسب استفاده کنید. [تنظیمات رمزنگاری Npgsql](https://www.npgsql.org/doc/security.html)

پیش از اجرای نسخهٔ جدید برنامه، همان ایمیج را یک بار با گزینهٔ `--migrate` اجرا کنید. برای مرحلهٔ migration از حساب دارای دسترسی DDL و در اجرای عادی API از حساب با دسترسی لازم به داده‌ها استفاده کنید. کلیدهای Data Protection در فایل نگهداری می‌شوند. دسترسی فایل و رمزنگاری در سطح ذخیره‌ساز به محیط استقرار وابسته است. از دست‌رفتن کلیدها، توکن‌ها و لینک‌های تأیید صادرشده را نامعتبر می‌کند.

در تنظیمات لاگ پراکسی، مقدار هدرهای `Authorization` و `X-Current-Password` و پارامترهای مسیر تأیید ایمیل را ثبت نکنید.

محدودیت نرخ در حافظهٔ هر نمونهٔ API اعمال می‌شود. در استقرار چندنمونه‌ای، محدودیت مشترک را در ورودی شبکه اعمال کنید. این نسخه برای هویت داخلی یک محصول طراحی شده و برای SSO یا فدراسیون هویت به ارائه‌دهندهٔ OIDC نیاز دارد.

## اجرای آزمون‌ها

```bash
docker compose up -d --wait database mail
dotnet restore --locked-mode
dotnet test -c Release --no-restore
```

آزمون‌های یکپارچه برای هر اجرای مجموعه، یک پایگاه دادهٔ موقت می‌سازند. ایمیل تأیید و کد بازیابی را از Mailpit می‌خوانند و احراز هویت، لغو نشست، تغییر مجوز، مرز مالکیت و محدودیت نرخ را از مسیر HTTP بررسی می‌کنند.

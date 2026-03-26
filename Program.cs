
using System.Globalization;
using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

var turkishCulture = new CultureInfo("tr-TR");
CultureInfo.DefaultThreadCurrentCulture = turkishCulture;
CultureInfo.DefaultThreadCurrentUICulture = turkishCulture;

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.Cookie.Name = "tamir-bakim-auth";
    });

builder.Services.AddAuthorization();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "AppData", "Keys")));
builder.Services.AddSingleton<AppStorage>();

var app = builder.Build();
var storage = app.Services.GetRequiredService<AppStorage>();
await storage.EnsureSeedDataAsync();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (HttpContext context) =>
{
    return context.User.Identity?.IsAuthenticated == true
        ? Results.Redirect("/dashboard")
        : Results.Redirect("/login");
});

app.MapGet("/login", (HttpContext context) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        return Results.Redirect("/dashboard");
    }

    var error = context.Request.Query["error"].ToString();
    return Results.Content(HtmlPages.Login(error), "text/html; charset=utf-8");
});

app.MapPost("/login", async (HttpContext context, AppStorage repo) =>
{
    var form = await context.Request.ReadFormAsync();
    var username = form["username"].ToString().Trim();
    var password = form["password"].ToString();

    var user = await repo.ValidateUserAsync(username, password);
    if (user is null)
    {
        return Results.Redirect("/login?error=Kullanici%20adi%20veya%20sifre%20hatali");
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id),
        new(ClaimTypes.Name, user.FullName),
        new("username", user.Username)
    };

    var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        principal,
        new AuthenticationProperties { IsPersistent = true });

    return Results.Redirect("/dashboard");
});

app.MapPost("/logout", [Authorize] async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.MapGet("/dashboard", [Authorize] async (HttpContext context, AppStorage repo) =>
{
    await repo.ArchiveCompletedRepairsAsync();
    var requests = await repo.GetAllRequestsAsync();
    var nextFormNumber = await repo.GetNextFormNumberAsync();
    var username = context.User.FindFirstValue("username") ?? "";
    var fullName = context.User.Identity?.Name ?? username;
    var message = context.Request.Query["message"].ToString();
    return Results.Content(HtmlPages.Dashboard(fullName, username, requests, nextFormNumber, message, AdminRules.IsAdmin(username)), "text/html; charset=utf-8");
});

app.MapGet("/dashboard/report", [Authorize] async (AppStorage repo) =>
{
    await repo.ArchiveCompletedRepairsAsync();
    var requests = await repo.GetAllRequestsAsync();
    var fileName = $"tamir-bakim-raporu-{Dates.Today():yyyy-MM-dd}.xlsx";
    var content = XlsxReportBuilder.BuildReport(requests);
    return Results.File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
});

app.MapGet("/history", [Authorize] async (HttpContext context, AppStorage repo) =>
{
    var username = context.User.FindFirstValue("username") ?? "";
    if (!AdminRules.IsAdmin(username))
    {
        return Results.Redirect("/dashboard?message=Arsiv%20ekranini%20sadece%20admin%20gorebilir");
    }
    await repo.ArchiveCompletedRepairsAsync();
    var search = context.Request.Query["q"].ToString().Trim();
    var requests = await repo.GetArchivedRequestsAsync(search);
    var message = context.Request.Query["message"].ToString();
    return Results.Content(HtmlPages.History(requests, search, message), "text/html; charset=utf-8");
});

app.MapGet("/history/report", [Authorize] async (HttpContext context, AppStorage repo) =>
{
    var username = context.User.FindFirstValue("username") ?? "";
    if (!AdminRules.IsAdmin(username))
    {
        return Results.Redirect("/dashboard?message=Arsiv%20raporunu%20sadece%20admin%20alabilir");
    }
    await repo.ArchiveCompletedRepairsAsync();
    var search = context.Request.Query["q"].ToString().Trim();
    var requests = await repo.GetArchivedRequestsAsync(search);
    var content = XlsxReportBuilder.BuildReport(requests);
    var fileName = $"tamir-bakim-arsiv-{Dates.Today():yyyy-MM-dd}.xlsx";
    return Results.File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
});

app.MapPost("/requests", [Authorize] async (HttpContext context, AppStorage repo) =>
{
    var form = await context.Request.ReadFormAsync();
    var currentUser = context.User.FindFirstValue("username") ?? "system";
    var requestDate = ParseDateOrToday(form["requestDate"].ToString());
    var deliveryDate = ParseOptionalDate(form["deliveryDate"].ToString());
    var sequenceNumber = await repo.GetNextSequenceNumberAsync();
    var formNumber = await repo.GetNextFormNumberAsync();

    var request = new MaintenanceRequest
    {
        Id = Guid.NewGuid().ToString("N"),
        WorkDate = requestDate.ToString("yyyy-MM-dd"),
        SequenceNumber = sequenceNumber,
        FormNumber = formNumber,
        OrderNumber = string.Empty,
        MaterialName = form["materialName"].ToString().Trim(),
        Quantity = form["quantity"].ToString().Trim(),
        Unit = form["unit"].ToString().Trim(),
        Description = form["description"].ToString().Trim(),
        DepartmentName = form["departmentName"].ToString().Trim(),
        OrderResponsible = form["orderResponsible"].ToString().Trim(),
        UnitManager = string.Empty,
        WarehouseResponsible = string.Empty,
        ApprovalOne = string.Empty,
        ApprovalTwo = string.Empty,
        DeliveredBy = string.Empty,
        DeliveryDate = deliveryDate,
        DeliveredBySignature = string.Empty,
        ReceivedBy = form["targetCompany"].ToString().Trim(),
        ReceivedBySignature = string.Empty,
        Status = string.IsNullOrWhiteSpace(form["status"]) ? RequestStatuses.WarrantySent : form["status"].ToString(),
        TrackingStatus = TrackingStatuses.Sent,
        CreatedAt = Dates.Now().ToString("yyyy-MM-dd HH:mm:ss"),
        CreatedBy = currentUser
    };

    if (!RequestStatuses.IsValid(request.Status))
    {
        return Results.Redirect("/dashboard?message=Gecersiz%20durum");
    }

    if (!MaintenanceRequestValidator.HasRequiredFields(request))
    {
        return Results.Redirect("/dashboard?message=Zorunlu%20alanlari%20doldurun");
    }

    await repo.AddRequestAsync(request);
    return Results.Redirect("/dashboard?message=Kayit%20eklendi");
});

app.MapGet("/requests/{id}/edit", [Authorize] async (string id, HttpContext context, AppStorage repo) =>
{
    var request = await repo.GetRequestByIdAsync(id);
    if (request is null)
    {
        return Results.Redirect("/dashboard?message=Kayit%20bulunamadi");
    }

    var username = context.User.FindFirstValue("username") ?? "";
    if (!Permissions.CanManageRequest(username, request))
    {
        return Results.Redirect("/dashboard?message=Bu%20kaydi%20duzenleme%20yetkiniz%20yok");
    }

    return Results.Content(HtmlPages.EditRequest(request, context.Request.Query["message"].ToString()), "text/html; charset=utf-8");
});
app.MapPost("/requests/{id}/edit", [Authorize] async (string id, HttpContext context, AppStorage repo) =>
{
    var existing = await repo.GetRequestByIdAsync(id);
    if (existing is null)
    {
        return Results.Redirect("/dashboard?message=Kayit%20bulunamadi");
    }

    var username = context.User.FindFirstValue("username") ?? "";
    if (!Permissions.CanManageRequest(username, existing))
    {
        return Results.Redirect("/dashboard?message=Bu%20kaydi%20duzenleme%20yetkiniz%20yok");
    }

    var form = await context.Request.ReadFormAsync();
    var updated = existing with
    {
        WorkDate = ParseDateOrToday(form["requestDate"].ToString()).ToString("yyyy-MM-dd"),
        FormNumber = existing.FormNumber,
        OrderNumber = string.Empty,
        MaterialName = form["materialName"].ToString().Trim(),
        Quantity = form["quantity"].ToString().Trim(),
        Unit = form["unit"].ToString().Trim(),
        Description = form["description"].ToString().Trim(),
        DepartmentName = form["departmentName"].ToString().Trim(),
        OrderResponsible = form["orderResponsible"].ToString().Trim(),
        UnitManager = string.Empty,
        WarehouseResponsible = string.Empty,
        ApprovalOne = string.Empty,
        ApprovalTwo = string.Empty,
        DeliveredBy = string.Empty,
        DeliveryDate = ParseOptionalDate(form["deliveryDate"].ToString()),
        DeliveredBySignature = string.Empty,
        ReceivedBy = form["targetCompany"].ToString().Trim(),
        ReceivedBySignature = string.Empty,
        Status = form["status"].ToString().Trim(),
        TrackingStatus = existing.TrackingStatus
    };

    if (!RequestStatuses.IsValid(updated.Status))
    {
        return Results.Redirect($"/requests/{id}/edit?message=Gecersiz%20durum");
    }

    if (!MaintenanceRequestValidator.HasRequiredFields(updated))
    {
        return Results.Redirect($"/requests/{id}/edit?message=Zorunlu%20alanlari%20doldurun");
    }

    await repo.UpdateRequestAsync(updated);
    return Results.Redirect("/dashboard?message=Kayit%20guncellendi");
});

app.MapPost("/requests/{id}/delete", [Authorize] async (string id, HttpContext context, AppStorage repo) =>
{
    var request = await repo.GetRequestByIdAsync(id);
    if (request is null)
    {
        return Results.Redirect("/dashboard?message=Kayit%20bulunamadi");
    }

    var username = context.User.FindFirstValue("username") ?? "";
    if (!Permissions.CanManageRequest(username, request))
    {
        return Results.Redirect("/dashboard?message=Bu%20kaydi%20silme%20yetkiniz%20yok");
    }

    var form = await context.Request.ReadFormAsync();
    var deleteReason = form["deleteReason"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(deleteReason))
    {
        return Results.Redirect("/dashboard?message=Silme%20sebebi%20zorunludur");
    }

    await repo.DeleteRequestAsync(id, username, deleteReason);
    return Results.Redirect("/dashboard?message=Kayit%20arsive%20tasindi");
});

app.MapPost("/requests/{id}/status", [Authorize] async (string id, HttpContext context, AppStorage repo) =>
{
    var request = await repo.GetRequestByIdAsync(id);
    if (request is null)
    {
        return Results.Redirect("/dashboard?message=Kayit%20bulunamadi");
    }

    var username = context.User.FindFirstValue("username") ?? "";
    if (!Permissions.CanManageRequest(username, request))
    {
        return Results.Redirect("/dashboard?message=Bu%20kaydi%20guncelleme%20yetkiniz%20yok");
    }

    var form = await context.Request.ReadFormAsync();
    var status = form["status"].ToString().Trim();
    if (!TrackingStatuses.IsValid(status))
    {
        return Results.Redirect("/dashboard?message=Gecersiz%20durum");
    }

    var updated = request with { TrackingStatus = status };
    if (status == TrackingStatuses.Repaired)
    {
        var returnDate = ParseOptionalDate(form["returnDate"].ToString());
        var receivedBackBy = form["receivedBackBy"].ToString().Trim();

        if (string.IsNullOrWhiteSpace(returnDate) || string.IsNullOrWhiteSpace(receivedBackBy))
        {
            return Results.Redirect("/dashboard?message=Geldigi%20tarih%20ve%20teslim%20alan%20zorunludur");
        }

        updated = updated with
        {
            ReturnDate = returnDate,
            ReceivedBackBy = receivedBackBy
        };
    }

    await repo.UpdateRequestAsync(updated);
    return Results.Redirect("/dashboard?message=Durum%20guncellendi");
});

app.MapGet("/users", [Authorize] async (HttpContext context, AppStorage repo) =>
{
    var currentUsername = context.User.FindFirstValue("username") ?? "";
    if (!AdminRules.IsAdmin(currentUsername))
    {
        return Results.Redirect("/dashboard?message=Bu%20alani%20sadece%20admin%20gorebilir");
    }

    var users = await repo.GetUsersAsync();
    var message = context.Request.Query["message"].ToString();
    return Results.Content(HtmlPages.Users(users, message), "text/html; charset=utf-8");
});

app.MapPost("/users", [Authorize] async (HttpContext context, AppStorage repo) =>
{
    var currentUsername = context.User.FindFirstValue("username") ?? "";
    if (!AdminRules.IsAdmin(currentUsername))
    {
        return Results.Redirect("/dashboard?message=Kullanici%20acma%20yetkiniz%20yok");
    }

    var form = await context.Request.ReadFormAsync();
    var fullName = form["fullName"].ToString().Trim();
    var username = form["username"].ToString().Trim();
    var password = form["password"].ToString();

    if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
    {
        return Results.Redirect("/users?message=Tum%20alanlari%20doldurun");
    }

    var result = await repo.AddUserAsync(fullName, username, password);
    return result
        ? Results.Redirect("/users?message=Kullanici%20eklendi")
        : Results.Redirect("/users?message=Bu%20kullanici%20adi%20zaten%20var");
});

app.MapPost("/users/{username}/delete", [Authorize] async (string username, HttpContext context, AppStorage repo) =>
{
    var currentUsername = context.User.FindFirstValue("username") ?? "";
    if (!AdminRules.IsAdmin(currentUsername))
    {
        return Results.Redirect("/dashboard?message=Kullanici%20silme%20yetkiniz%20yok");
    }

    var deleted = await repo.DeleteUserAsync(username, currentUsername);
    return deleted
        ? Results.Redirect("/users?message=Kullanici%20silindi")
        : Results.Redirect("/users?message=Bu%20kullanici%20silinemez");
});

app.MapPost("/users/{username}/reset-password", [Authorize] async (string username, HttpContext context, AppStorage repo) =>
{
    var currentUsername = context.User.FindFirstValue("username") ?? "";
    if (!AdminRules.IsAdmin(currentUsername))
    {
        return Results.Redirect("/dashboard?message=Parola%20sifirlama%20yetkiniz%20yok");
    }

    var form = await context.Request.ReadFormAsync();
    var password = form["password"].ToString();
    if (string.IsNullOrWhiteSpace(password))
    {
        return Results.Redirect("/users?message=Yeni%20parola%20bos%20olamaz");
    }

    var updated = await repo.UpdateUserPasswordAsync(username, password);
    return updated
        ? Results.Redirect("/users?message=Parola%20guncellendi")
        : Results.Redirect("/users?message=Kullanici%20bulunamadi");
});

app.MapGet("/account", [Authorize] async (HttpContext context, AppStorage repo) =>
{
    var username = context.User.FindFirstValue("username") ?? "";
    var user = await repo.GetUserByUsernameAsync(username);
    if (user is null)
    {
        return Results.Redirect("/dashboard?message=Kullanici%20bulunamadi");
    }

    var message = context.Request.Query["message"].ToString();
    return Results.Content(HtmlPages.Account(user, message), "text/html; charset=utf-8");
});

app.MapPost("/account/password", [Authorize] async (HttpContext context, AppStorage repo) =>
{
    var username = context.User.FindFirstValue("username") ?? "";
    var form = await context.Request.ReadFormAsync();
    var password = form["password"].ToString();

    if (string.IsNullOrWhiteSpace(password))
    {
        return Results.Redirect("/account?message=Sifre%20bos%20birakilamaz");
    }

    var updated = await repo.UpdateUserPasswordAsync(username, password);
    return updated
        ? Results.Redirect("/account?message=Sifre%20guncellendi")
        : Results.Redirect("/account?message=Kullanici%20bulunamadi");
});

app.Run("http://0.0.0.0:5079");

static DateOnly ParseDateOrToday(string? text)
{
    if (!string.IsNullOrWhiteSpace(text))
    {
        var formats = new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd" };
        foreach (var format in formats)
        {
            if (DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return parsed;
            }
        }
    }

    return Dates.Today();
}

static string ParseOptionalDate(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return string.Empty;
    }

    var parsed = ParseDateOrToday(text);
    return parsed.ToString("yyyy-MM-dd");
}

static class AdminRules
{
    public static bool IsAdmin(string username) => username.Equals("admin", StringComparison.OrdinalIgnoreCase);
}

static class Permissions
{
    public static bool CanManageRequest(string username, MaintenanceRequest request)
    {
        var todayText = Dates.Today().ToString("yyyy-MM-dd");
        return request.WorkDate == todayText || AdminRules.IsAdmin(username);
    }
}

static class RequestStatuses
{
    public const string WarrantySent = "Garantiye Gonderildi";
    public const string MaintenanceSent = "Bakima Gonderildi";
    public const string RepairSent = "Tamire Gonderildi";

    public static readonly string[] All = [WarrantySent, MaintenanceSent, RepairSent];

    public static bool IsValid(string status) => All.Contains(status);

    public static string RowClass(string status) => status switch
    {
        WarrantySent => "row-blue",
        MaintenanceSent => "row-green",
        RepairSent => "row-orange",
        _ => ""
    };

    public static string BadgeClass(string status) => status switch
    {
        WarrantySent => "status-blue",
        MaintenanceSent => "status-green",
        RepairSent => "status-orange",
        _ => "status-gray"
    };
}

static class TrackingStatuses
{
    public const string Sent = "Gonderildi";
    public const string Repaired = "Tamir Yapildi";
    public const string Cancelled = "Iptal Edildi";

    public static readonly string[] All = [Sent, Repaired, Cancelled];

    public static bool IsValid(string status) => All.Contains(status);

    public static string RowClass(string status) => status switch
    {
        Sent => "row-yellow",
        Repaired => "row-green",
        Cancelled => "row-red",
        _ => string.Empty
    };

    public static string BadgeClass(string status) => status switch
    {
        Sent => "status-yellow",
        Repaired => "status-green",
        Cancelled => "status-red",
        _ => "status-gray"
    };
}

static class MaintenanceRequestValidator
{
    public static bool HasRequiredFields(MaintenanceRequest request)
    {
        return new[]
        {
            request.MaterialName,
            request.Quantity,
            request.Unit,
            request.Description,
            request.DepartmentName
        }.All(value => !string.IsNullOrWhiteSpace(value));
    }
}
static class XlsxReportBuilder
{
    public static byte[] BuildReport(List<MaintenanceRequest> requests)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", ContentTypes);
            WriteEntry(archive, "_rels/.rels", RootRels);
            WriteEntry(archive, "docProps/app.xml", AppXml);
            WriteEntry(archive, "docProps/core.xml", CoreXml);
            WriteEntry(archive, "xl/workbook.xml", WorkbookXml);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
            WriteEntry(archive, "xl/styles.xml", StylesXml);
            WriteEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(requests));
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string BuildWorksheetXml(List<MaintenanceRequest> requests)
    {
        var rows = new StringBuilder();
        var headers = new[] { "Tarih", "Form No", "Malzeme Cinsi", "Miktar", "Birim", "Bolum", "Durum", "Aciklama" };
        rows.Append("<row r=\"1\" spans=\"1:8\" x14ac:dyDescent=\"0.3\">");
        for (var i = 0; i < headers.Length; i++)
        {
            rows.Append(HeaderCell($"{ColumnName(i + 1)}1", headers[i]));
        }
        rows.Append("</row>");

        for (var i = 0; i < requests.Count; i++)
        {
            var rowNumber = i + 2;
            var request = requests[i];
            var values = new[]
            {
                request.WorkDate,
                request.FormNumber,
                request.MaterialName,
                request.Quantity,
                request.Unit,
                request.DepartmentName,
                request.Status,
                request.Description
            };

            rows.Append($"<row r=\"{rowNumber}\" spans=\"1:8\" x14ac:dyDescent=\"0.3\">");
            for (var column = 0; column < values.Length; column++)
            {
                rows.Append(TextCell($"{ColumnName(column + 1)}{rowNumber}", values[column]));
            }
            rows.Append("</row>");
        }

        var lastRow = Math.Max(1, requests.Count + 1);
        return $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" mc:Ignorable="x14ac xr xr2 xr3" xmlns:x14ac="http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac" xmlns:xr="http://schemas.microsoft.com/office/spreadsheetml/2014/revision" xmlns:xr2="http://schemas.microsoft.com/office/spreadsheetml/2015/revision2" xmlns:xr3="http://schemas.microsoft.com/office/spreadsheetml/2016/revision3">
  <dimension ref="A1:H{{lastRow}}"/>
  <sheetViews><sheetView workbookViewId="0"><selection sqref="A1:H{{lastRow}}"/></sheetView></sheetViews>
  <sheetFormatPr defaultRowHeight="14.4" x14ac:dyDescent="0.3"/>
  <cols>
    <col min="1" max="1" width="14" customWidth="1"/>
    <col min="2" max="2" width="12" customWidth="1"/>
    <col min="3" max="3" width="14" customWidth="1"/>
    <col min="4" max="4" width="24" customWidth="1"/>
    <col min="5" max="5" width="12" customWidth="1"/>
    <col min="6" max="6" width="10" customWidth="1"/>
    <col min="7" max="7" width="18" customWidth="1"/>
    <col min="8" max="8" width="42" customWidth="1"/>
  </cols>
  <sheetData>{{rows}}</sheetData>
  <pageMargins left="0.7" right="0.7" top="0.75" bottom="0.75" header="0.3" footer="0.3"/>
</worksheet>
""";
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        while (index > 0)
        {
            index--;
            name = (char)('A' + index % 26) + name;
            index /= 26;
        }

        return name;
    }

    private static string HeaderCell(string reference, string text) => $"<c r=\"{reference}\" s=\"1\" t=\"inlineStr\"><is><t>{Xml(text)}</t></is></c>";
    private static string TextCell(string reference, string text) => $"<c r=\"{reference}\" s=\"1\" t=\"inlineStr\"><is><t>{Xml(text)}</t></is></c>";
    private static string Xml(string? text) => System.Security.SecurityElement.Escape(text ?? string.Empty) ?? string.Empty;

    private const string ContentTypes = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
  <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
  <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
</Types>
""";

    private const string RootRels = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
  <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
</Relationships>
""";

    private const string AppXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
  <Application>Microsoft Excel</Application>
</Properties>
""";

    private const string CoreXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:dcmitype="http://purl.org/dc/dcmitype/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <dc:creator>Codex</dc:creator>
  <cp:lastModifiedBy>Codex</cp:lastModifiedBy>
</cp:coreProperties>
""";

    private const string WorkbookXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <sheets><sheet name="Rapor" sheetId="1" r:id="rId1"/></sheets>
</workbook>
""";

    private const string WorkbookRelsXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>
""";

    private const string StylesXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <fonts count="1"><font><sz val="11"/><color theme="1"/><name val="Calibri"/><family val="2"/><charset val="162"/><scheme val="minor"/></font></fonts>
  <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
  <borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style="thin"><color indexed="64"/></left><right style="thin"><color indexed="64"/></right><top style="thin"><color indexed="64"/></top><bottom style="thin"><color indexed="64"/></bottom><diagonal/></border></borders>
  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
  <cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1"/></cellXfs>
  <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
</styleSheet>
""";
}

static class Dates
{
    private static readonly TimeZoneInfo Zone = ResolveZone();

    public static DateTime Now() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).DateTime;
    public static DateOnly Today() => DateOnly.FromDateTime(Now());

    private static TimeZoneInfo ResolveZone()
    {
        var ids = new[] { "Turkey Standard Time", "Europe/Istanbul" };
        foreach (var id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
            }
        }

        return TimeZoneInfo.Local;
    }
}

sealed class AppStorage
{
    private readonly string _usersPath;
    private readonly string _requestsPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AppStorage(IWebHostEnvironment env)
    {
        var dataDir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dataDir);
        _usersPath = Path.Combine(dataDir, "users.json");
        _requestsPath = Path.Combine(dataDir, "requests.json");
    }

    public async Task EnsureSeedDataAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_usersPath))
            {
                await WriteAsync(_usersPath, new List<AppUser> { AppUser.Create("admin", "System.01", "Sistem Yonetici") });
            }

            if (!File.Exists(_requestsPath))
            {
                await WriteAsync(_requestsPath, new List<MaintenanceRequest>());
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<AppUser>> GetUsersAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await ReadAsync<AppUser>(_usersPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AppUser?> GetUserByUsernameAsync(string username)
    {
        var users = await GetUsersAsync();
        return users.FirstOrDefault(user => user.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AppUser?> ValidateUserAsync(string username, string password)
    {
        var users = await GetUsersAsync();
        return users.FirstOrDefault(user =>
            user.Username.Equals(username, StringComparison.OrdinalIgnoreCase) &&
            PasswordHasher.Verify(password, user.PasswordHash));
    }

    public async Task<bool> AddUserAsync(string fullName, string username, string password)
    {
        await _lock.WaitAsync();
        try
        {
            var users = await ReadAsync<AppUser>(_usersPath);
            if (users.Any(user => user.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            users.Add(AppUser.Create(username, password, fullName));
            await WriteAsync(_usersPath, users);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }
    public async Task<bool> UpdateUserPasswordAsync(string username, string password)
    {
        await _lock.WaitAsync();
        try
        {
            var users = await ReadAsync<AppUser>(_usersPath);
            var index = users.FindIndex(user => user.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            users[index] = users[index] with { PasswordHash = PasswordHasher.Hash(password) };
            await WriteAsync(_usersPath, users);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteUserAsync(string username, string currentUsername)
    {
        await _lock.WaitAsync();
        try
        {
            if (username.Equals("admin", StringComparison.OrdinalIgnoreCase) || username.Equals(currentUsername, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var users = await ReadAsync<AppUser>(_usersPath);
            var removed = users.RemoveAll(user => user.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                return false;
            }

            await WriteAsync(_usersPath, users);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<MaintenanceRequest>> GetRequestsByDateAsync(DateOnly date)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync<MaintenanceRequest>(_requestsPath);
            var dateText = date.ToString("yyyy-MM-dd");
            return all.Where(x => x.WorkDate == dateText).OrderBy(x => x.SequenceNumber == 0 ? int.MaxValue : x.SequenceNumber).ThenBy(x => x.CreatedAt).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<MaintenanceRequest>> GetAllRequestsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync<MaintenanceRequest>(_requestsPath);
            return all
                .Where(x => !x.IsDeleted)
                .OrderBy(x => int.TryParse(x.FormNumber, out var formNumber) ? formNumber : int.MaxValue)
                .ThenBy(x => x.CreatedAt)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> GetNextSequenceNumberAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync<MaintenanceRequest>(_requestsPath);
            var max = all.Select(x => x.SequenceNumber).DefaultIfEmpty(0).Max();
            return max + 1;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> GetNextFormNumberAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync<MaintenanceRequest>(_requestsPath);
            var usedNumbers = all
                .Where(x => !x.IsDeleted)
                .Select(x => int.TryParse(x.FormNumber, out var number) ? number : 0)
                .Where(number => number > 0)
                .Distinct()
                .OrderBy(number => number)
                .ToHashSet();

            var next = 1;
            while (usedNumbers.Contains(next))
            {
                next++;
            }

            return next.ToString("D5");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<DateOnly>> GetArchivedDatesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync<MaintenanceRequest>(_requestsPath);
            return all
                .Where(x => x.IsDeleted)
                .Select(x => DateOnly.ParseExact(x.WorkDate, "yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Distinct()
                .OrderByDescending(x => x)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddRequestAsync(MaintenanceRequest request)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync<MaintenanceRequest>(_requestsPath);
            all.Add(request);
            await WriteAsync(_requestsPath, all);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<MaintenanceRequest?> GetRequestByIdAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync<MaintenanceRequest>(_requestsPath);
            return all.FirstOrDefault(x => x.Id == id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<MaintenanceRequest>> GetArchivedRequestsByDateAsync(DateOnly date)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync<MaintenanceRequest>(_requestsPath);
            var dateText = date.ToString("yyyy-MM-dd");
            return all
                .Where(x => x.IsDeleted && x.WorkDate == dateText)
                .OrderBy(x => int.TryParse(x.FormNumber, out var formNumber) ? formNumber : int.MaxValue)
                .ThenBy(x => x.DeletedAt)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<MaintenanceRequest>> GetArchivedRequestsAsync(string search)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync<MaintenanceRequest>(_requestsPath);
            var archived = all
                .Where(x => x.IsDeleted)
                .OrderByDescending(x => x.DeletedAt)
                .ThenByDescending(x => x.CreatedAt)
                .ToList();

            if (string.IsNullOrWhiteSpace(search))
            {
                return archived;
            }

            return archived.Where(x =>
                    Contains(x.FormNumber, search) ||
                    Contains(x.MaterialName, search) ||
                    Contains(x.DepartmentName, search) ||
                    Contains(x.ReceivedBy, search) ||
                    Contains(x.OrderResponsible, search) ||
                    Contains(x.Description, search) ||
                    Contains(x.DeletedBy, search) ||
                    Contains(x.DeleteReason, search))
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateRequestAsync(MaintenanceRequest updated)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync<MaintenanceRequest>(_requestsPath);
            var index = all.FindIndex(x => x.Id == updated.Id);
            if (index >= 0)
            {
                all[index] = updated;
                await WriteAsync(_requestsPath, all);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteRequestAsync(string id, string deletedBy, string deleteReason)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync<MaintenanceRequest>(_requestsPath);
            var index = all.FindIndex(x => x.Id == id);
            if (index >= 0)
            {
                all[index] = all[index] with
                {
                    IsDeleted = true,
                    DeletedAt = Dates.Now().ToString("yyyy-MM-dd HH:mm:ss"),
                    DeletedBy = deletedBy,
                    DeleteReason = deleteReason
                };
                await WriteAsync(_requestsPath, all);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ArchiveCompletedRepairsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync<MaintenanceRequest>(_requestsPath);
            var today = Dates.Today().ToString("yyyy-MM-dd");
            var changed = false;

            for (var i = 0; i < all.Count; i++)
            {
                var item = all[i];
                if (item.IsDeleted || item.TrackingStatus != TrackingStatuses.Repaired || item.WorkDate == today)
                {
                    continue;
                }

                all[i] = item with
                {
                    IsDeleted = true,
                    DeletedAt = Dates.Now().ToString("yyyy-MM-dd HH:mm:ss"),
                    DeletedBy = "Sistem",
                    DeleteReason = "Gece otomatik arsiv"
                };
                changed = true;
            }

            if (changed)
            {
                await WriteAsync(_requestsPath, all);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool Contains(string value, string search) =>
        value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    private async Task<List<T>> ReadAsync<T>(string path)
    {
        if (!File.Exists(path))
        {
            return new List<T>();
        }

        await using var stream = File.OpenRead(path);
        var data = await JsonSerializer.DeserializeAsync<List<T>>(stream, _jsonOptions);
        return data ?? new List<T>();
    }

    private async Task WriteAsync<T>(string path, List<T> items)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, items, _jsonOptions);
    }
}

sealed record class AppUser
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Username { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string PasswordHash { get; init; } = string.Empty;

    public static AppUser Create(string username, string password, string fullName) => new()
    {
        Username = username,
        FullName = fullName,
        PasswordHash = PasswordHasher.Hash(password)
    };
}

sealed record class MaintenanceRequest
{
    public string Id { get; init; } = string.Empty;
    public string WorkDate { get; init; } = string.Empty;
    public int SequenceNumber { get; init; }
    public string FormNumber { get; init; } = string.Empty;
    public string OrderNumber { get; init; } = string.Empty;
    public string MaterialName { get; init; } = string.Empty;
    public string Quantity { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DepartmentName { get; init; } = string.Empty;
    public string OrderResponsible { get; init; } = string.Empty;
    public string UnitManager { get; init; } = string.Empty;
    public string WarehouseResponsible { get; init; } = string.Empty;
    public string ApprovalOne { get; init; } = string.Empty;
    public string ApprovalTwo { get; init; } = string.Empty;
    public string DeliveredBy { get; init; } = string.Empty;
    public string DeliveryDate { get; init; } = string.Empty;
    public string DeliveredBySignature { get; init; } = string.Empty;
    public string ReceivedBy { get; init; } = string.Empty;
    public string ReceivedBySignature { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string TrackingStatus { get; init; } = string.Empty;
    public string ReturnDate { get; init; } = string.Empty;
    public string ReceivedBackBy { get; init; } = string.Empty;
    public bool IsDeleted { get; init; }
    public string DeletedAt { get; init; } = string.Empty;
    public string DeletedBy { get; init; } = string.Empty;
    public string DeleteReason { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public string CreatedBy { get; init; } = string.Empty;
}

static class PasswordHasher
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 2)
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

static class HtmlPages
{
    public static string Login(string error)
    {
        var alert = string.IsNullOrWhiteSpace(error) ? string.Empty : $"<div class='alert'>{Encode(error)}</div>";
        return $$"""
<!DOCTYPE html>
<html lang="tr">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Akguc Seramik Tamir ve Bakim Talep Sistemi</title>
  <style>{{Css}}</style>
</head>
<body class="login-body">
  <main class="login-card">
    <h1>Akguc Seramik Tamir ve Bakim Talep Sistemi</h1>
    <p>Fabrikadan tamir veya bakima gonderilen urunleri kaydedin.</p>
    {{alert}}
    <form method="post" action="/login" class="stack">
      <label>Kullanici Adi<input name="username" required /></label>
      <label>Sifre<input type="password" name="password" required /></label>
      <button type="submit">Giris Yap</button>
    </form>
    <p class="hint">Varsayilan kullanici: <strong>admin</strong> / <strong>System.01</strong></p>
  </main>
</body>
</html>
""";
    }

    public static string Dashboard(string fullName, string username, List<MaintenanceRequest> requests, string nextFormNumber, string message, bool isAdmin)
    {
        var alert = string.IsNullOrWhiteSpace(message) ? string.Empty : $"<div class='alert success'>{Encode(message)}</div>";
        var usersLink = isAdmin ? "<a href='/users'>Kullanicilar</a>" : string.Empty;
        var archiveLink = isAdmin ? "<a href='/history'>Arsiv</a>" : string.Empty;
        var rows = requests.Count == 0
            ? $"<tr><td colspan='{(isAdmin ? 9 : 8)}'>Kayit bulunmuyor.</td></tr>"
            : string.Join(string.Empty, requests.Select(r => $$"""
<tr class="{{TrackingStatuses.RowClass(r.TrackingStatus)}}">
  <td>{{r.SequenceNumber}}</td>
  <td>{{Encode(r.FormNumber)}}</td>
  <td>{{Encode(r.MaterialName)}}</td>
  <td>{{Encode(r.Quantity)}} {{Encode(r.Unit)}}</td>
  <td>{{Encode(r.DepartmentName)}}</td>
  <td class="detail-cell">
    <div><strong>Gonderim:</strong> <span class="badge {{RequestStatuses.BadgeClass(r.Status)}}">{{Encode(r.Status)}}</span></div>
    <div><strong>Firma:</strong> {{Encode(r.ReceivedBy)}}</div>
    <div><strong>Sorumlu:</strong> {{Encode(r.OrderResponsible)}}</div>
    <div><strong>Teslim Tarihi:</strong> {{Encode(FormatDateDisplay(r.DeliveryDate))}}</div>
    {{(string.IsNullOrWhiteSpace(r.ReturnDate) ? "" : $"<div><strong>Geldigi Tarih:</strong> {Encode(FormatDateDisplay(r.ReturnDate))}</div>")}}
    {{(string.IsNullOrWhiteSpace(r.ReceivedBackBy) ? "" : $"<div><strong>Teslim Alan:</strong> {Encode(r.ReceivedBackBy)}</div>")}}
  </td>
  <td class="detail-cell">{{Encode(r.Description)}}</td>
  <td>{{StatusButtons(r)}}</td>
  {{ActionCell(r, isAdmin)}}
</tr>
"""));

        return $$"""
<!DOCTYPE html>
<html lang="tr">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Akguc Seramik Tamir ve Bakim Talep Sistemi</title>
  <style>{{Css}}</style>
</head>
<body>
  <div class="topbar">
    <div>
      <strong>Akguc Seramik Tamir ve Bakim Talep Sistemi</strong>
      <div class="subtle">Tum kayitlar - {{Encode(fullName)}} ({{Encode(username)}})</div>
    </div>
    <div class="top-actions">
      {{archiveLink}}
      <a href="/account">Hesabim</a>
      {{usersLink}}
      <form method="post" action="/logout"><button type="submit">Cikis</button></form>
    </div>
  </div>
  <main class="single-page">
    <section class="card">
      <div class="table-head">
        <div>
          <h2>Tum Kayitlar</h2>
          <p class="subtle">Kayitlar gun sonunda sifirlanmaz, ana ekranda kalir.</p>
          <div class="status-guide-inline">
            <span><strong>Sari:</strong> Gonderildi</span>
            <span><strong>Yesil:</strong> Tamir Yapildi</span>
            <span><strong>Kirmizi:</strong> Iptal Edildi</span>
          </div>
        </div>
        <div class="table-actions">
          <button type="button" onclick="document.getElementById('request-modal').showModal()">Talep Ekle</button>
          <a class="action-link" href="/dashboard/report" download>Rapor Al</a>
        </div>
      </div>
      {{alert}}
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Sira</th>
              <th>Form No</th>
              <th>Malzeme</th>
              <th>Miktar</th>
              <th>Bolum</th>
              <th>Detay</th>
              <th>Aciklama</th>
              <th>Takip</th>
              {{(isAdmin ? "<th>Islem</th>" : "")}}
            </tr>
          </thead>
          <tbody>{{rows}}</tbody>
        </table>
      </div>
    </section>
  </main>

  <dialog id="request-modal" class="modal">
    <div class="modal-head">
      <div>
        <h3>Talep Ekle</h3>
        <p class="subtle">Yeni tamir ve bakim kaydini buradan girin.</p>
      </div>
      <button type="button" class="ghost" onclick="document.getElementById('request-modal').close()">Kapat</button>
    </div>
    <form method="post" action="/requests" class="form-grid">
      {{RequestFields(null, nextFormNumber)}}
      <div class="full modal-actions">
        <button type="button" class="ghost" onclick="document.getElementById('request-modal').close()">Vazgec</button>
        <button type="submit">Talebi Kaydet</button>
      </div>
    </form>
  </dialog>

  <script>
    setInterval(function () {
      var modal = document.getElementById('request-modal');
      if (modal && modal.open) {
        return;
      }
      window.location.reload();
    }, 15000);
  </script>
</body>
</html>
""";
    }

    public static string History(List<MaintenanceRequest> requests, string search, string message)
    {
        var alert = string.IsNullOrWhiteSpace(message) ? string.Empty : $"<div class='alert success'>{Encode(message)}</div>";
        var rows = requests.Count == 0
            ? $"<tr><td colspan='12'>Aramaya uygun arsiv kaydi bulunmuyor.</td></tr>"
            : string.Join(string.Empty, requests.Select(r => $$"""
<tr class="{{TrackingStatuses.RowClass(r.TrackingStatus)}}">
  <td>{{r.SequenceNumber}}</td>
  <td>{{Encode(r.FormNumber)}}</td>
  <td>{{Encode(r.MaterialName)}}</td>
  <td>{{Encode(r.Quantity)}} {{Encode(r.Unit)}}</td>
  <td>{{Encode(r.DepartmentName)}}</td>
  <td><span class="badge {{RequestStatuses.BadgeClass(r.Status)}}">{{Encode(r.Status)}}</span></td>
  <td>{{Encode(r.ReceivedBy)}}</td>
  <td>{{Encode(r.CreatedBy)}}</td>
  <td>{{Encode(r.Description)}}</td>
  <td>{{Encode(r.DeletedBy)}}</td>
  <td>{{Encode(r.DeleteReason)}}</td>
  <td>{{Encode(FormatDateTimeDisplay(r.DeletedAt))}}</td>
</tr>
"""));

        return $$"""
<!DOCTYPE html>
<html lang="tr">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Akguc Seramik Tamir ve Bakim Talep Sistemi</title>
  <style>{{Css}}</style>
</head>
<body>
  <div class="topbar">
    <div><strong>Akguc Seramik Tamir ve Bakim Talep Sistemi</strong><div class="subtle">Arsiv ekrani</div></div>
    <div class="top-actions"><a href="/dashboard">Panele Don</a><a href="/account">Hesabim</a></div>
  </div>
  <main class="single-page">
    <section class="card">
      <div class="table-head">
        <div><h2>Arsiv Kayitlari</h2><p class="subtle">Silinen ve gece otomatik arsive tasinan kayitlar burada tutulur.</p></div>
        <a class="action-link" href="/history/report?q={{Uri.EscapeDataString(search)}}" download>Rapor Al</a>
      </div>
      <form method="get" action="/history" class="history-form archive-search">
        <label>Arsivde Ara
          <input name="q" value="{{Encode(search)}}" placeholder="Firma, malzeme, form no, silme sebebi..." />
        </label>
        <button type="submit">Ara</button>
      </form>
      {{alert}}
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Sira</th><th>Form No</th><th>Malzeme</th><th>Miktar</th><th>Bolum</th><th>Gonderim</th><th>Firma</th><th>Olusturan</th><th>Aciklama</th><th>Silen</th><th>Silme Sebebi</th><th>Silinme Tarihi</th>
            </tr>
          </thead>
          <tbody>{{rows}}</tbody>
        </table>
      </div>
    </section>
  </main>
</body>
</html>
""";
    }

    public static string EditRequest(MaintenanceRequest request, string message)
    {
        var alert = string.IsNullOrWhiteSpace(message) ? string.Empty : $"<div class='alert'>{Encode(message)}</div>";
        var returnUrl = request.WorkDate == Dates.Today().ToString("yyyy-MM-dd") ? "/dashboard" : $"/history?date={request.WorkDate}";
        return $$"""
<!DOCTYPE html>
<html lang="tr">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Akguc Seramik Tamir ve Bakim Talep Sistemi</title>
  <style>{{Css}}</style>
</head>
<body>
  <div class="topbar">
    <div><strong>Akguc Seramik Tamir ve Bakim Talep Sistemi</strong><div class="subtle">Talep duzenleme</div></div>
    <div class="top-actions"><a href="{{returnUrl}}">Geri Don</a></div>
  </div>
  <main class="single-page">
    <section class="card form-card full-width">
      <h2>Talebi Duzenle</h2>
      {{alert}}
      <form method="post" action="/requests/{{request.Id}}/edit" class="form-grid">
        {{RequestFields(request)}}
        <div class="full action-row"><a class="action-link" href="{{returnUrl}}">Vazgec</a><button type="submit">Kaydi Guncelle</button></div>
      </form>
    </section>
  </main>
</body>
</html>
""";
    }

    public static string Users(List<AppUser> users, string message)
    {
        var alert = string.IsNullOrWhiteSpace(message) ? string.Empty : $"<div class='alert success'>{Encode(message)}</div>";
        var rows = string.Join(string.Empty, users.Select(user => $$"""
<tr>
  <td>{{Encode(user.FullName)}}</td>
  <td>{{Encode(user.Username)}}</td>
  <td>
    <form method="post" action="/users/{{Encode(user.Username)}}/reset-password" class="inline-form">
      <input type="password" name="password" placeholder="Yeni parola" required />
      <button type="submit">Sifirla</button>
    </form>
  </td>
  <td>
    {{(user.Username.Equals("admin", StringComparison.OrdinalIgnoreCase)
      ? "<span class='subtle'>Silinemez</span>"
      : $"<form method='post' action='/users/{Encode(user.Username)}/delete' onsubmit=\"return confirm('Bu kullaniciyi silmek istiyor musunuz?')\"><button class='danger' type='submit'>Sil</button></form>")}}
  </td>
</tr>
"""));

        return $$"""
<!DOCTYPE html>
<html lang="tr">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Akguc Seramik Tamir ve Bakim Talep Sistemi</title>
  <style>{{Css}}</style>
</head>
<body>
  <div class="topbar">
    <div><strong>Akguc Seramik Tamir ve Bakim Talep Sistemi</strong><div class="subtle">Kullanici yonetimi</div></div>
    <div class="top-actions"><a href="/dashboard">Panele Don</a></div>
  </div>
  <main class="single-page users-page">
    <section class="card form-card">
      <h2>Yeni Kullanici</h2>
      <form method="post" action="/users" class="form-grid users-grid">
        <label>Ad Soyad<input name="fullName" required /></label>
        <label>Kullanici Adi<input name="username" required /></label>
        <label>Sifre<input type="password" name="password" required /></label>
        <div class="full"><button type="submit">Kullaniciyi Ekle</button></div>
      </form>
    </section>
    <section class="card">
      {{alert}}
      <table>
        <thead><tr><th>Ad Soyad</th><th>Kullanici</th><th>Parola</th><th>Sil</th></tr></thead>
        <tbody>{{rows}}</tbody>
      </table>
    </section>
  </main>
</body>
</html>
""";
    }

    public static string Account(AppUser user, string message)
    {
        var alert = string.IsNullOrWhiteSpace(message) ? string.Empty : $"<div class='alert success'>{Encode(message)}</div>";
        return $$"""
<!DOCTYPE html>
<html lang="tr">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Akguc Seramik Tamir ve Bakim Talep Sistemi</title>
  <style>{{Css}}</style>
</head>
<body>
  <div class="topbar">
    <div><strong>Akguc Seramik Tamir ve Bakim Talep Sistemi</strong><div class="subtle">Hesap ayarlari</div></div>
    <div class="top-actions"><a href="/dashboard">Panele Don</a></div>
  </div>
  <main class="single-page">
    <section class="card form-card">
      <h2>Hesabim</h2>
      <p class="subtle">Kullanici adi: <strong>{{Encode(user.Username)}}</strong></p>
      {{alert}}
      <form method="post" action="/account/password" class="stack">
        <label>Yeni Sifre<input type="password" name="password" required /></label>
        <button type="submit">Parolayi Degistir</button>
      </form>
    </section>
  </main>
</body>
</html>
""";
    }

    private static string RequestFields(MaintenanceRequest? request, string nextFormNumber = "00001")
    {
        var requestDate = FormatDateInput(request?.WorkDate, Dates.Today());
        var deliveryDate = FormatDateInput(request?.DeliveryDate, Dates.Today());
        var status = request?.Status ?? RequestStatuses.WarrantySent;
        var formNumber = request?.FormNumber ?? nextFormNumber;
        var options = string.Join(string.Empty, RequestStatuses.All.Select(item => $"<option value='{Encode(item)}' {(item == status ? "selected" : string.Empty)}>{Encode(item)}</option>"));

        return $$"""
<label>Tarih *<input name="requestDate" value="{{Encode(requestDate)}}" placeholder="GG.AA.YYYY" required /></label>
<label>Form No<input name="formNumber" value="{{Encode(formNumber)}}" readonly /></label>
<label>Malzeme Cinsi *<input name="materialName" value="{{Encode(request?.MaterialName ?? string.Empty)}}" required /></label>
<label>Miktar *<input name="quantity" value="{{Encode(request?.Quantity ?? string.Empty)}}" required /></label>
<label>Birim *<input name="unit" value="{{Encode(request?.Unit ?? string.Empty)}}" placeholder="Adet / Kg / Mt" required /></label>
<label class="full">Aciklama *<textarea name="description" rows="3" required>{{Encode(request?.Description ?? string.Empty)}}</textarea></label>
<label>Bolum Adi *<input name="departmentName" value="{{Encode(request?.DepartmentName ?? string.Empty)}}" required /></label>
<label>Siparis Sorumlusu<input name="orderResponsible" value="{{Encode(request?.OrderResponsible ?? string.Empty)}}" /></label>
<label>Teslim Tarihi<input name="deliveryDate" value="{{Encode(deliveryDate)}}" placeholder="GG.AA.YYYY" /></label>
<label>Gonderilecek Firma<input name="targetCompany" value="{{Encode(request?.ReceivedBy ?? string.Empty)}}" /></label>
<label>Durum<select name="status">{{options}}</select></label>
""";
    }

    private static string StatusButtons(MaintenanceRequest request)
    {
        return string.Join(string.Empty, TrackingStatuses.All.Select(status => $$"""
<form method="post" action="/requests/{{request.Id}}/status" class="status-form" {{(status == TrackingStatuses.Repaired ? "onsubmit=\"var returnDate = prompt('Geldigi tarihi giriniz (GG.AA.YYYY)'); if (!returnDate || !returnDate.trim()) { alert('Geldigi tarih zorunludur'); return false; } var receivedBackBy = prompt('Teslim alani giriniz'); if (!receivedBackBy || !receivedBackBy.trim()) { alert('Teslim alan zorunludur'); return false; } this.querySelector('input[name=returnDate]').value = returnDate.trim(); this.querySelector('input[name=receivedBackBy]').value = receivedBackBy.trim(); return true;\"" : "")}}>
  <input type="hidden" name="status" value="{{Encode(status)}}" />
  <input type="hidden" name="returnDate" value="" />
  <input type="hidden" name="receivedBackBy" value="" />
  <button type="submit" class="status-btn {{TrackingStatuses.BadgeClass(status)}} {{(request.TrackingStatus == status ? "active" : string.Empty)}}">{{Encode(status)}}</button>
</form>
"""));
    }

    private static string ActionCell(MaintenanceRequest request, bool isAdmin)
    {
        if (!isAdmin)
        {
            return string.Empty;
        }

        return $$"""
<td>
  <div class="action-row">
    <a class="action-link" href="/requests/{{request.Id}}/edit">Duzenle</a>
    <form method="post" action="/requests/{{request.Id}}/delete" onsubmit="var reason = prompt('Silinme sebebini giriniz'); if (!reason || !reason.trim()) { alert('Silme sebebi zorunludur'); return false; } this.querySelector('input[name=deleteReason]').value = reason.trim(); return confirm('Bu kayit arsive tasinacak. Devam edilsin mi?');">
      <input type="hidden" name="deleteReason" value="" />
      <button class="danger" type="submit">Sil</button>
    </form>
  </div>
</td>
""";
    }

    private static string Encode(string value) => System.Net.WebUtility.HtmlEncode(value);

    private static string FormatDateInput(string? rawDate, DateOnly fallback)
    {
        if (string.IsNullOrWhiteSpace(rawDate))
        {
            return fallback.ToString("dd.MM.yyyy");
        }

        return FormatDateDisplay(rawDate);
    }

    private static string FormatDateDisplay(string? rawDate)
    {
        if (string.IsNullOrWhiteSpace(rawDate))
        {
            return string.Empty;
        }

        var formats = new[] { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy" };
        foreach (var format in formats)
        {
            if (DateOnly.TryParseExact(rawDate, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return parsed.ToString("dd.MM.yyyy");
            }
        }

        return rawDate;
    }

    private static string FormatDateTimeDisplay(string? rawDateTime)
    {
        if (string.IsNullOrWhiteSpace(rawDateTime))
        {
            return string.Empty;
        }

        var formats = new[] { "yyyy-MM-dd HH:mm:ss", "dd.MM.yyyy HH:mm:ss" };
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(rawDateTime, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return parsed.ToString("dd.MM.yyyy HH:mm:ss");
            }
        }

        return rawDateTime;
    }

    private const string Css = """
body { margin: 0; font-family: "Segoe UI", sans-serif; background: #f6f3ed; color: #1f2937; }
a { color: #fef3c7; text-decoration: none; }
button, input, textarea, select { font: inherit; }
button { border: 0; border-radius: 10px; padding: 10px 16px; background: #b45309; color: white; cursor: pointer; font-weight: 700; }
button.danger { background: #b91c1c; }
input, textarea, select { border: 1px solid #d8cbb2; border-radius: 10px; padding: 10px 12px; background: white; }
label { display: grid; gap: 6px; font-size: 14px; font-weight: 600; }
.login-body { min-height: 100vh; display: grid; place-items: center; background: linear-gradient(135deg, #f7ead7, #fffaf3 45%, #f2ece4); }
.login-card, .card { background: #fffdf8; border-radius: 18px; box-shadow: 0 18px 50px rgba(87, 64, 32, 0.12); }
.login-card { width: min(420px, calc(100vw - 32px)); padding: 32px; }
.topbar { display: flex; justify-content: space-between; align-items: center; gap: 16px; padding: 20px 28px; background: #2f4858; color: white; }
.top-actions, .action-row, .inline-form { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.single-page { padding: 24px; }
.users-page { display: grid; gap: 20px; }
.card { padding: 24px; }
.form-card { max-width: 100%; }
.full-width { max-width: none; }
.form-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 14px; }
.users-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
.stack, .history-form { display: grid; gap: 14px; }
.full { grid-column: 1 / -1; }
.table-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 12px; margin-bottom: 16px; }
.status-guide-inline { display: flex; gap: 14px; flex-wrap: wrap; margin-top: 10px; font-size: 13px; color: #6b7280; }
.table-actions, .modal-head, .modal-actions { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.table-wrap { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; border: 1px solid #d8cbb2; background: white; }
th, td { padding: 12px 10px; border-bottom: 1px solid #efe7db; border-right: 1px solid #eadfcd; text-align: left; font-size: 14px; vertical-align: top; }
th:last-child, td:last-child { border-right: 0; }
th { background: #f6ead9; }
.subtle, .hint { color: #6b7280; }
.alert { padding: 12px 14px; border-radius: 10px; background: #fee2e2; color: #991b1b; margin-bottom: 12px; }
.alert.success { background: #dcfce7; color: #166534; }
.badge, .action-link { display: inline-flex; align-items: center; justify-content: center; min-height: 38px; padding: 0 12px; border-radius: 10px; font-weight: 700; }
.action-link { background: #e7ded1; color: #3f3427; }
.status-form { display: inline-flex; margin: 2px; }
.status-btn { padding: 6px 8px; min-height: 32px; border-radius: 8px; font-size: 11px; white-space: nowrap; }
.detail-cell { min-width: 210px; line-height: 1.55; }
.status-yellow { background: #ca8a04; color: #fff8e1; }
.status-blue { background: #2563eb; color: white; }
.status-orange { background: #ea580c; color: white; }
.status-green { background: #15803d; color: white; }
.status-red { background: #dc2626; color: white; }
.status-gray { background: #64748b; color: white; }
.ghost { background: #e7ded1; color: #3f3427; }
.active { box-shadow: 0 0 0 3px rgba(15, 23, 42, 0.18) inset; }
.row-yellow td { background: #fef3c7; }
.row-blue td { background: #dbeafe; }
.row-orange td { background: #fed7aa; }
.row-green td { background: #dcfce7; }
.row-red td { background: #fee2e2; }
.modal { width: min(1100px, calc(100vw - 24px)); border: 0; border-radius: 18px; padding: 24px; box-shadow: 0 24px 80px rgba(87, 64, 32, 0.28); }
.modal::backdrop { background: rgba(47, 72, 88, 0.45); }
@media (max-width: 980px) { .users-page, .form-grid, .users-grid { grid-template-columns: 1fr; } .topbar, .table-head, .table-actions, .modal-head, .modal-actions { flex-direction: column; align-items: flex-start; } .modal { width: calc(100vw - 16px); padding: 16px; } }
""";
}

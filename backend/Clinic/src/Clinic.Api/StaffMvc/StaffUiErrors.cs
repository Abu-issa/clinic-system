using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Clinic.Api.StaffMvc;

[AttributeUsage(AttributeTargets.Method)]
public sealed class StaffDownloadUiAttribute : Attribute { }

public static class StaffUiErrors
{
    // Opt-in presentation on the existing download endpoint; never changes its authorization or bytes.
    public static bool IsStaffDownload(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<StaffDownloadUiAttribute>() is not null &&
        context.Request.Query["ui"].Count == 1 && context.Request.Query["ui"] == "staff";
    public static IResult HttpResult(int status)
    {
        var result = Result(status);
        return Results.Content(result.Content, result.ContentType, statusCode: status);
    }
    public static string Key(int status) => status switch
    {
        400 => "Csrf", 401 => "401", 403 => "403", 404 => "404", 409 => "Transition",
        413 => "TooLarge", 415 => "Unsupported", _ => "500"
    };
    public static ContentResult Result(int status, string? key = null)
    {
        var t = new StaffText();
        string E(string value) => HtmlEncoder.Default.Encode(value);
        var css = t.IsArabic ? "bootstrap.rtl.min.css" : "bootstrap.min.css";
        return new ContentResult { StatusCode = status, ContentType = "text/html; charset=utf-8",
            Content = $"<!doctype html><html lang='{t.Culture}' dir='{(t.IsArabic ? "rtl" : "ltr")}'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>{E(t["Error"])}</title><link rel='stylesheet' href='/vendor/bootstrap/{css}'><link rel='stylesheet' href='/staff-assets/tests.css'></head><body><main class='container py-5'><div class='card p-4'><h1>{E(t["Error"])}</h1><p role='alert'>{E(t[key ?? Key(status)])}</p><p>{status}</p></div></main></body></html>" };
    }
    public static async Task WriteAsync(HttpContext context, int status)
    {
        var result = Result(status);
        context.Response.StatusCode = status;
        context.Response.ContentType = result.ContentType;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(result.Content!);
    }
}

// Also handles resource/antiforgery filter short circuits without changing REST responses.
public sealed class StaffUiResultFilter : Attribute, IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        var status = context.Result switch
        {
            ObjectResult { Value: ProblemDetails problem } => problem.Status ?? 500,
            StatusCodeResult code => code.StatusCode,
            _ => 0
        };
        if (status < 400) return;
        if (status is 409 or 413 or 415 && context.RouteData.Values.ContainsKey("requestId"))
        {
            // A resource-gate short circuit occurs before MVC creates the controller.
            var tempData = context.HttpContext.RequestServices.GetRequiredService<ITempDataDictionaryFactory>()
                .GetTempData(context.HttpContext);
            tempData["Notice"] = StaffUiErrors.Key(status);
            tempData.Save();
            context.Result = new RedirectToActionResult("Detail", "StaffTestsMvc", new {
                patientId = context.RouteData.Values["patientId"], requestId = context.RouteData.Values["requestId"],
                culture = new StaffText().Culture });
        }
        else context.Result = StaffUiErrors.Result(status);
    }
    public void OnResultExecuted(ResultExecutedContext context) { }
}

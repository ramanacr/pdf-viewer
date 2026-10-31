using System.Runtime.InteropServices.JavaScript;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PdfViewer.Web;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The page's own script: canvas drawing, file reading, keyboard and scroll observation. Nothing else is loaded.
await JSHost.ImportAsync(PdfViewer.Web.Interop.CanvasInterop.ModuleName, "../js/viewer.js?v=12");

await builder.Build().RunAsync();

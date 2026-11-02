using Winser.Helpers;
using Winser.Models;

var engine = SearchEngine.Default;
Check("example.com", "https://example.com", true);
Check("example.com/path?q=1", "https://example.com/path?q=1", true);
Check("http://example.com", "http://example.com", true);
Check("https://example.com", "https://example.com", true);
Check("https://user:secret@example.com", "https://user:secret@example.com", true);
Check("http://user:secret@example.com", "http://user:secret@example.com", true);
Check("localhost:3000", "http://localhost:3000", true);
Check("localhost:3000/path", "http://localhost:3000/path", true);
Check("localhost", "http://localhost", true);
Check("api.localhost:3000", "http://api.localhost:3000", true);
Check("127.0.0.1:5173", "http://127.0.0.1:5173", true);
Check("127.0.0.2:5173", "http://127.0.0.2:5173", true);
Check("[::1]:8080", "http://[::1]:8080", true);
Check("192.168.1.1", "https://192.168.1.1", true);
Check("//example.com/path", "https://example.com/path", true);
Check("//user:secret@example.com/path", new Uri("https://user:secret@example.com/path").AbsoluteUri, true);
Check("//", engine.BuildSearchUrl("//"), false);
Check("//example.com:99999", engine.BuildSearchUrl("//example.com:99999"), false);
Check("example.com:99999", engine.BuildSearchUrl("example.com:99999"), false);
Check("http://example.com:99999", engine.BuildSearchUrl("http://example.com:99999"), false);
Check("http://", engine.BuildSearchUrl("http://"), false);
Check("", InternalPages.NewTab, false);
Check("hello world", engine.BuildSearchUrl("hello world"), false);
Check("winser://settings", InternalPages.Settings, true);

var filePath = @"C:\Users\Someone\my file.html";
Check(filePath, new Uri(filePath).AbsoluteUri, true);

if (UrlHelper.ForDisplay("http://localhost:3000/path") != "http://localhost:3000/path" ||
    UrlHelper.Security("http://localhost:3000/path") != PageSecurity.Insecure ||
    UrlHelper.Security("https://example.com") != PageSecurity.Secure)
{
    throw new InvalidOperationException("Address display or page security changed unexpectedly.");
}

void Check(string input, string expected, bool navigable)
{
    var actual = UrlHelper.Resolve(input, engine);
    if (actual != expected || UrlHelper.LooksNavigable(input) != navigable)
    {
        throw new InvalidOperationException(
            $"{input}: expected '{expected}' / navigable={navigable}, got '{actual}' / navigable={UrlHelper.LooksNavigable(input)}");
    }
}

Console.WriteLine("UrlHelper checks passed.");

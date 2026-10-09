using System.Text;

namespace vamos.Services;

/// <summary>
/// Writes the finished survey report to a plain-text file on the server.
/// Files end up in <c>data/private/submissions</c> (git-ignored).
/// </summary>
public sealed class SubmissionWriter
{
    private readonly IWebHostEnvironment _environment;

    public SubmissionWriter(IWebHostEnvironment environment) => _environment = environment;

    public async Task<string> WriteAsync(string content)
    {
        var directory = Path.Combine(_environment.ContentRootPath, "data", "private", "submissions");
        Directory.CreateDirectory(directory);

        var fileName = $"atsakymai-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        var path = Path.Combine(directory, fileName);

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}

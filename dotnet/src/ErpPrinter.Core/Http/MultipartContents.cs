using System.Net.Http.Headers;
using System.Text;

namespace ErpPrinter.Http;

public static class MultipartContents
{
    /// <summary>
    /// Builds multipart/form-data with string fields and one streamed file part. The caller must dispose the
    /// returned content, which also closes the file.
    /// </summary>
    public static MultipartFormDataContent Create(
        IEnumerable<KeyValuePair<string, string>> fields,
        string fileField,
        string filePath,
        string fileName,
        string mediaType = "application/pdf")
    {
        var multipart = new MultipartFormDataContent();
        try
        {
            foreach (var (name, value) in fields)
                multipart.Add(new StringContent(value, Encoding.UTF8), name);

            var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var fileContent = new StreamContent(file);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
            multipart.Add(fileContent, fileField, fileName);
            return multipart;
        }
        catch
        {
            multipart.Dispose();
            throw;
        }
    }
}

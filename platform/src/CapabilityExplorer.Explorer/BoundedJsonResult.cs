using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CapabilityExplorer.Explorer;

/// <summary>Bounds serialized uncompressed output before headers/body are sent.</summary>
internal sealed class BoundedJsonResult(object value, int statusCode = 200) : IResult
{
    public const int MaximumBytes = 1024 * 1024;

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        using var stream = new LimitedStream();
        var options = httpContext.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        try
        {
            using var writer = new Utf8JsonWriter(stream);
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
            writer.Flush();
        }
        catch (ResponseLimitException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await httpContext.Response.WriteAsJsonAsync(new { error = "response exceeds one MiB; narrow the request or reduce the page size" }, httpContext.RequestAborted);
            return;
        }
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        httpContext.Response.ContentLength = stream.Length;
        stream.Position = 0;
        await stream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
    }

    private sealed class ResponseLimitException : Exception;

    private sealed class LimitedStream : MemoryStream
    {
        public LimitedStream() : base(4096) { }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count); base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length); base.Write(buffer);
        }
        private void Check(int count)
        {
            if (count > MaximumBytes - Position) throw new ResponseLimitException();
            var needed = checked((int)Position + count);
            if (needed > Capacity) Capacity = Math.Min(MaximumBytes, Math.Max(needed, Capacity * 2));
        }
    }
}

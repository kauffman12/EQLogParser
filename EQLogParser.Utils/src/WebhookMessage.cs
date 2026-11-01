using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EQLogParser
{
  internal sealed record WebhookMessage(string BatchId, int Part, int Total, bool Aggregated, string Digest,
    string Content)
  {
    private const int MaxContentLength = 2000;

    internal string Json => Aggregated || Total > 1
      ? JsonSerializer.Serialize(new
      {
        content = Content,
        embeds = new[]
        {
          new
          {
            footer = new
            {
              text = $"EQLP1|{BatchId}|{Part}|{Total}|{(Aggregated ? "A" : "N")}|{Digest}"
            }
          }
        }
      })
      : JsonSerializer.Serialize(new { content = Content });

    internal static List<WebhookMessage> Create(string content, bool aggregated, string batchId = null)
    {
      ArgumentException.ThrowIfNullOrEmpty(content);
      var id = batchId == null ? Guid.NewGuid() : Guid.ParseExact(batchId, "N");
      // Canonical UUID byte order matches its hexadecimal representation, not Guid's default mixed-endian bytes.
      var encodedBatchId = Base64Url.EncodeToString(id.ToByteArray(bigEndian: true));
      var digest = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

      var chunks = new List<string>();
      for (var offset = 0; offset < content.Length;)
      {
        var length = Math.Min(MaxContentLength, content.Length - offset);
        if (offset + length < content.Length)
        {
          // Pack whole lines when possible; a single oversized line still needs Unicode-safe splitting.
          var newline = content.LastIndexOf('\n', offset + length - 1, length);
          if (newline > offset)
          {
            length = newline - offset + 1;
          }
          else if ((char.IsHighSurrogate(content[offset + length - 1]) && char.IsLowSurrogate(content[offset + length]))
            || (content[offset + length - 1] == '\r' && content[offset + length] == '\n'))
          {
            length--;
          }
        }

        chunks.Add(content.Substring(offset, length));
        offset += length;
      }

      var result = new List<WebhookMessage>();
      for (var index = 0; index < chunks.Count; index++)
      {
        result.Add(new(encodedBatchId, index + 1, chunks.Count, aggregated, digest, chunks[index]));
      }

      return result;
    }
  }
}
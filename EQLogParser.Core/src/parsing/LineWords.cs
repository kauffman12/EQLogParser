namespace EQLogParser
{
  /*
   * A word view over a line's action text with the indexing of `string.Split(' ') and none of its strings.
   *
   * Why: one Split(' ') per recognized line is ~374 B and 29 % of ingest stage time (DesignNotes → "Where ingest
   * spends its time, stage by stage") — it is the widest single allocation in the process, and almost every word it
   * creates is only ever COMPARED (to a literal) and thrown away. This type scans the source once for space
   * positions, stores them in one int[], and hands out `Word` views: same ordinal comparisons, same name slicing,
   * and a real string materializes only where a word (or a range of words) becomes something a record OWNS —
   * which is exactly what was already being built before.
   *
   * The semantics are `Split(' ')`'s, empty entries included: "a  b" is THREE tokens ("a", "", "b"), a leading space
   * starts an empty token, and a trailing space ends with one. A tokenizer that skipped double spaces would shift
   * every index downstream and silently rewrite names — LineWordsTest pins equivalence over a corpus for that reason,
   * not as decoration.
   */
  internal sealed class LineWords
  {
    // Shared instance for null/empty action text; old code guarded `split is { Length: >= 2 }` against the null array,
    // which reads the same as Length == 0 here.
    internal static readonly LineWords Empty = new(string.Empty, [], 0);

    private readonly string _source;
    private readonly int[] _starts; // start offset of every token; token i ends where token i+1 begins (minus its space)
    private readonly int _length;

    private LineWords(string source, int[] starts, int length)
    {
      _source = source;
      _starts = starts;
      _length = length;
    }

    /// <summary>Token count, matching `action.Split(' ').Length`.</summary>
    internal int Length => _length;

    /// <summary>Word i of the line. Index semantics match the array's (out of range throws).</summary>
    internal Word this[int index]
      => new(_source, _starts[index], EndOf(index) - _starts[index]);

    /// <summary>Token for the whole source string — a plain full Split, for the legacy `LineData.Split` door.</summary>
    internal string[] ToArray() => _length == 0 ? [] : _source.Split(' ');

    internal static LineWords Build(string action)
    {
      // Null only: "" must build ONE empty token because that is what "".Split(' ') returns — the equivalence
      // this type exists on. (A null Action had no old behavior to keep; the door used to throw.)
      if (action is null)
      {
        return Empty;
      }

      // Two cheap scans beat grow-copy: one counts tokens (every space ends one), the second records start offsets.
      var starts = new int[CountSpaces(action) + 1];
      var n = 1;
      starts[0] = 0;
      for (var i = 0; i < action.Length; i++)
      {
        if (action[i] == ' ')
        {
          starts[n++] = i + 1;
        }
      }

      return new LineWords(action, starts, n);
    }

    private static int CountSpaces(string s)
    {
      var count = 0;
      for (var i = 0; i < s.Length; i++)
      {
        if (s[i] == ' ')
        {
          count++;
        }
      }

      return count;
    }

    private int EndOf(int index) => index + 1 < _length ? _starts[index + 1] - 1 : _source.Length;

    internal bool IsEmpty(int index) => EndOf(index) == _starts[index];

    /// <summary>The token as a real string. Use only where the value outlives the line (record fields, caches).</summary>
    internal string StringAt(int index) => this[index].ToString();

    /// <summary>
    /// Port of ParserUtil.FindStop over the word view — same rules, same result; LineWordsTest pins them equal on a
    /// corpus so the two can never drift quietly.
    /// </summary>
    internal int FindStop()
    {
      var stop = _length - 1;
      if (stop < 0)
      {
        return -1;
      }

      var last = this[stop];
      if (!last.IsEmpty && last[last.Length - 1] == ')')
      {
        for (var i = stop; i >= 0 && stop > 2; i--)
        {
          var w = this[i];
          if (!w.IsEmpty && w[0] == '(')
          {
            stop = i - 1;
            break;
          }
        }
      }

      return stop;
    }

    /// <summary>TextUtils.ParseUInt over the token span — identical to TextUtils.ParseUInt(split[index]).</summary>
    internal uint ParseUInt(int index) => TextUtils.ParseUInt(this[index].Span);

    /// <summary>
    /// Join a range with single spaces, SKIPPING empty words — the ParserUtil.JoinWords contract, ported exactly,
    /// including its >256-char fallback which joins WITHOUT the skip (the old long path called string.Join directly;
    /// bit-identical behavior wins over tidiness here).
    /// </summary>
    internal unsafe string JoinWords(int start, int count)
    {
      if (count <= 0)
      {
        return string.Empty;
      }

      if (count == 1)
      {
        return this[start].ToString();
      }

      var totalLength = 0;
      var wordCount = 0;
      for (var i = 0; i < count; i++)
      {
        var len = this[start + i].Length;
        if (len > 0)
        {
          totalLength += len;
          wordCount++;
        }
      }

      totalLength += wordCount > 1 ? wordCount - 1 : 0;

      if (totalLength <= 256)
      {
        Span<char> buffer = stackalloc char[totalLength];
        var pos = 0;
        var first = true;
        for (var i = 0; i < count; i++)
        {
          var w = this[start + i];
          if (!w.IsEmpty)
          {
            if (!first)
            {
              buffer[pos++] = ' ';
            }

            w.Span.CopyTo(buffer.Slice(pos));
            pos += w.Length;
            first = false;
          }
        }

        unsafe
        {
          fixed (char* p = buffer)
          {
            return new string(p, 0, pos);
          }
        }
      }

      return JoinRaw(start, count);
    }

    /// <summary>Plain join of a range (empty tokens included) — the `string.Join(" ", split, start, count)` contract.</summary>
    internal string JoinRaw(int start, int count)
    {
      if (count <= 0)
      {
        return string.Empty;
      }

      var total = count - 1;
      for (var i = 0; i < count; i++)
      {
        total += this[start + i].Length;
      }

      return string.Create(total, (words: this, start, count), static (span, state) =>
      {
        var pos = 0;
        for (var i = 0; i < state.count; i++)
        {
          if (i > 0)
          {
            span[pos++] = ' ';
          }

          state.words[state.start + i].Span.CopyTo(span.Slice(pos));
          pos += state.words[state.start + i].Length;
        }
      });
    }

    /// <summary>One token of the line: a (source, start, length) view. Comparison semantics are ordinal, like == on strings.</summary>
    internal readonly struct Word(string source, int start, int length)
    {
      private readonly string _wordSource = source;
      private readonly int _wordStart = start;
      private readonly int _wordLength = length;

      internal int Length => _wordLength;

      internal bool IsEmpty => _wordLength == 0;

      internal ReadOnlySpan<char> Span => _wordSource.AsSpan(_wordStart, _wordLength);

      internal char this[int index] => _wordSource[_wordStart + index];

      internal char this[System.Index index]
        => _wordSource[index.GetOffset(_wordLength) + _wordStart];

      internal bool EndsWith(char value) => !IsEmpty && _wordSource[_wordStart + _wordLength - 1] == value;

      internal bool Contains(char value) => Span.IndexOf(value) > -1;

      internal bool StartsWith(char value) => !IsEmpty && _wordSource[_wordStart] == value;

      internal bool StartsWith(string value) => Span.StartsWith(value.AsSpan(), StringComparison.Ordinal);

      internal bool StartsWith(string value, StringComparison comparison) => Span.StartsWith(value.AsSpan(), comparison);

      internal bool EndsWith(string value, StringComparison comparison) => Span.EndsWith(value.AsSpan(), comparison);

      /// <summary>Materialize the token. Same value as `split[i]` ever was.</summary>
      public override string ToString() => _wordLength == 0 ? string.Empty : _wordSource.Substring(_wordStart, _wordLength);

      /// <summary>Ordinal equality — exactly what `split[i] == "literal"` did.</summary>
      public static bool operator ==(Word word, string text)
        => text is not null && word._wordLength == text.Length && word.Span.SequenceEqual(text.AsSpan());

      public static bool operator !=(Word word, string text) => !(word == text);

      public static bool operator ==(string text, Word word) => word == text;

      public static bool operator !=(string text, Word word) => !(word == text);

      public override bool Equals(object obj)
        => obj switch
        {
          Word other => _wordLength == other._wordLength && Span.SequenceEqual(other.Span),
          string text => this == text,
          _ => false,
        };

      // Ordinal-consistent with == ; a loop because no allocation-free span hash exists on this framework target.
      // Nothing in the parsers hashes a Word — this only keeps the type honest if someone ever puts one in a set.
      public override int GetHashCode()
      {
        var hash = 17;
        for (var i = 0; i < _wordLength; i++)
        {
          hash = (hash * 31) + _wordSource[_wordStart + i];
        }

        return hash;
      }
    }
  }
}

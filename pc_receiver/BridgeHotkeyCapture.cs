using System;
using System.Collections.Generic;
using System.Linq;

namespace pc_receiver;

public sealed class BridgeHotkeyCaptureSession
{
    private readonly List<string> _tokens = [];

    public IReadOnlyList<string> Tokens => _tokens;

    public void Reset()
    {
        _tokens.Clear();
    }

    public void Observe(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || _tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _tokens.Add(token);
    }
}

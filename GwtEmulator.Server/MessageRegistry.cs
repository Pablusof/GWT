using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GwtEmulator.Protocol;

namespace GwtEmulator.Server;

public class MessageRegistry
{
    private static readonly Dictionary<TkMsgType, Type> _portals = new();
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;

        var assembly = typeof(MessageRegistry).Assembly;
        // Also scan Protocol assembly
        var assemblies = new[] { assembly, typeof(IRequest).Assembly };

        foreach (var asm in assemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                var attr = type.GetCustomAttribute<MessagePortalAttribute>();
                if (attr != null && typeof(IRequest).IsAssignableFrom(type))
                {
                    if (!_portals.ContainsKey(attr.PortalId))
                    {
                        _portals.Add(attr.PortalId, type);
                    }
                }
            }
        }

        _initialized = true;
        Console.WriteLine($"[MessageRegistry] Registered {_portals.Count} message types");
    }

    public static IRequest? CreateMessage(TkMsgType msgType)
    {
        if (!_portals.TryGetValue(msgType, out var type))
            return null;

        try
        {
            return (IRequest)Activator.CreateInstance(type)!;
        }
        catch
        {
            return null;
        }
    }

    public static int RegisteredCount => _portals.Count;
}

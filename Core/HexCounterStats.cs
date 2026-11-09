using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using RelicStats.Core;

namespace HextechRelicStats;

/// <summary>Finds other mods' types at runtime, so we never compile against their DLLs.</summary>
public static class Hex
{
    public static Assembly? Assembly => Find("HextechRunes");

    public static Assembly? Find(string assemblyName) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName);

    /// <summary>A method on HextechRunes.&lt;typeName&gt; (public or protected). Throws if missing, so the patch is skipped and logged.</summary>
    public static MethodBase Method(string typeName, string methodName) =>
        Method("HextechRunes", typeName, methodName);

    /// <summary>A method on &lt;assembly&gt;.&lt;typeName&gt; (namespace = assembly name).</summary>
    public static MethodBase Method(string assemblyName, string typeName, string methodName)
    {
        var type = Find(assemblyName)?.GetType(assemblyName + "." + typeName)
                   ?? throw new InvalidOperationException($"{assemblyName}.{typeName} not found (mod not installed?)");
        return AccessTools.DeclaredMethod(type, methodName)
               ?? throw new InvalidOperationException($"{typeName}.{methodName} not found");
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using NameMapResource;
using s3pi.Interfaces;
using s3pi.Package;
using ScriptResource;

internal static class Sims3PackageBuilder
{
    private const uint S3SAType = 0x073FAA07;
    private const uint XmlType = 0x0333406C;
    private const uint KeyType = 0x0166038C;
    private const uint Group = 0x00000000;
    private const string AssemblyResourceName = "SimsOnlineServerHostMod";
    private const string InstantiatorXmlName = "SimsOnlineServerHost.Initializer";
    // The client presentation module (a second script assembly; SimsOnlineServerHostMod/PresentationModules.cs).
    private const string ModuleResourceName = "SimsOnlinePresentation";

    private static string sS3PePath;

    private sealed class ResourceKey : AResourceKey
    {
        public override int RecommendedApiVersion
        {
            get { return 1; }
        }

        public override List<string> ContentFields
        {
            get { return AApiVersionedFields.GetContentFields(requestedApiVersion, GetType()); }
        }

        public ResourceKey(uint resourceType, uint resourceGroup, ulong instance)
            : base(1, null, resourceType, resourceGroup, instance)
        {
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: Sims3PackageBuilder <dllPath> <packagePath> <s3pePath> [moduleDllPath]");
            return 2;
        }

        sS3PePath = args[2];
        AppDomain.CurrentDomain.AssemblyResolve += ResolveS3PiAssembly;

        try
        {
            BuildPackage(args[0], args[1], args.Length > 3 ? args[3] : null);
            Console.WriteLine("Packaged " + args[1]);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static Assembly ResolveS3PiAssembly(object sender, ResolveEventArgs args)
    {
        string assemblyPath = Path.Combine(sS3PePath, new AssemblyName(args.Name).Name + ".dll");
        if (!File.Exists(assemblyPath))
        {
            return null;
        }

        return Assembly.LoadFrom(assemblyPath);
    }

    private static void BuildPackage(string dllPath, string packagePath, string moduleDllPath)
    {
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException("Missing script assembly.", dllPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(packagePath));

        ulong assemblyHash = Hash64(AssemblyResourceName);
        ulong instantiatorHash = Hash64(InstantiatorXmlName);

        byte[] dllData = File.ReadAllBytes(dllPath);
        Stream s3saStream = NewScriptResourceStream(dllData);
        Stream xmlStream = new MemoryStream(Encoding.UTF8.GetBytes(InitializerXml()));
        bool hasModule = moduleDllPath != null && File.Exists(moduleDllPath);
        ulong moduleHash = Hash64(ModuleResourceName);
        Stream keyStream = NewKeyResourceStream(assemblyHash, instantiatorHash, hasModule ? moduleHash : 0UL);

        IPackage package = Package.NewPackage(1);
        try
        {
            package.AddResource(new ResourceKey(S3SAType, Group, assemblyHash), s3saStream, true);
            package.AddResource(new ResourceKey(XmlType, Group, instantiatorHash), xmlStream, true);
            package.AddResource(new ResourceKey(KeyType, Group, 0), keyStream, true);
            if (hasModule)
            {
                package.AddResource(new ResourceKey(S3SAType, Group, moduleHash), NewScriptResourceStream(File.ReadAllBytes(moduleDllPath)), true);
            }
            package.SaveAs(packagePath);
        }
        finally
        {
            Package.ClosePackage(1, package);
        }
    }

    private static Stream NewScriptResourceStream(byte[] assemblyData)
    {
        ScriptResource.ScriptResource scriptResource = new ScriptResource.ScriptResource(1, null);
        using (MemoryStream assemblyStream = new MemoryStream(assemblyData))
        using (BinaryReader assemblyReader = new BinaryReader(assemblyStream))
        {
            scriptResource.Assembly = assemblyReader;
        }

        return scriptResource.Stream;
    }

    private static Stream NewKeyResourceStream(ulong assemblyHash, ulong instantiatorHash, ulong moduleHash)
    {
        NameMapResource.NameMapResource nameMap = new NameMapResource.NameMapResource(1, null);
        nameMap.Add(assemblyHash, AssemblyResourceName);
        nameMap.Add(instantiatorHash, InstantiatorXmlName);
        if (moduleHash != 0UL)
        {
            nameMap.Add(moduleHash, ModuleResourceName);
        }
        return nameMap.Stream;
    }

    private static string InitializerXml()
    {
        // SIMSONLINE_METHOD_SWAP=1 on the build: the package carries kMethodSwap True (Initializer.cs, MethodSwap.cs).
        bool methodSwap = Environment.GetEnvironmentVariable("SIMSONLINE_METHOD_SWAP") == "1";
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<base>\r\n" +
            "  <Current_Tuning>\r\n" +
            "    <kInstantiator value=\"True\" />\r\n" +
            (methodSwap ? "    <kMethodSwap value=\"True\" />\r\n" : "") +
            "  </Current_Tuning>\r\n" +
            "</base>\r\n";
    }

    private static ulong Hash64(string value)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char ch in value)
        {
            int code = ch;
            if (code >= 65 && code <= 90)
            {
                code += 32;
            }

            unchecked
            {
                hash = (hash * 1099511628211UL) ^ (byte)code;
            }
        }

        return hash;
    }
}

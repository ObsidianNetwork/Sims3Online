using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // The object-creation boundary (internal documentation). Every object the game makes (script objects and
    // props without a script, like the wooden spoon or an eating utensil from GlobalFunctions.CreateProp) is created by
    // Sims3.SimIFace.Simulator through ISimulator.CreateObject(prototypeKey, overrideData), from the private static
    // Simulator.gSimulator the engine sets from AppDomain data ("Simulator"). This decorator is swapped in there on the
    // server and keeps, for each created object, its prototype key; a destroy forgets it. Object identity (O lines) then
    // describes any object by the key it was made from, so a client can make the same, script or not.
    // Everything else passes straight through. It never throws into the game.
    internal sealed class SimulatorRecorder : ISimulator
    {
        private const int Capacity = 8192;
        private static readonly Dictionary<ulong, ResourceKey> sCreated = new Dictionary<ulong, ResourceKey>();
        private static readonly Queue<ulong> sOrder = new Queue<ulong>();
        internal static long Creates;
        internal static long Destroys;

        private readonly ISimulator mInner;

        private SimulatorRecorder(ISimulator inner)
        {
            mInner = inner;
        }

        private static FieldInfo SimulatorField()
        {
            return typeof(Simulator).GetField("gSimulator", BindingFlags.NonPublic | BindingFlags.Static);
        }

        internal static bool Installed
        {
            get
            {
                FieldInfo field = SimulatorField();
                return field != null && field.GetValue(null) is SimulatorRecorder;
            }
        }

        internal static string Install()
        {
            try
            {
                FieldInfo field = SimulatorField();
                if (field == null)
                {
                    return "simulator=unavailable";
                }

                object current = field.GetValue(null);
                if (current is SimulatorRecorder)
                {
                    return "simulator=on";
                }

                ISimulator inner = current as ISimulator;
                if (inner == null)
                {
                    return "simulator=no-instance";
                }

                field.SetValue(null, new SimulatorRecorder(inner));
                return "simulator=on";
            }
            catch (Exception ex)
            {
                return "simulator=error:" + ex.GetType().Name;
            }
        }

        // The prototype key an object was created from on this game (since the recorder was installed).
        internal static bool TryGetKey(ulong id, out ResourceKey key)
        {
            return sCreated.TryGetValue(id, out key);
        }

        internal static int Count
        {
            get { return sCreated.Count; }
        }

        public ObjectGuid CurrentTask
        {
            get { return mInner.CurrentTask; }
        }

        public long TickCount
        {
            get { return mInner.TickCount; }
        }

        public bool IsInitDone(ObjectGuid handle)
        {
            return mInner.IsInitDone(handle);
        }

        public ObjectGuid CreateObject(ResourceKey prototypeKey, Hashtable overrideData)
        {
            ObjectGuid id = mInner.CreateObject(prototypeKey, overrideData);
            try
            {
                if (id.IsValid)
                {
                    if (!sCreated.ContainsKey(id.Value))
                    {
                        while (sCreated.Count >= Capacity && sOrder.Count > 0)
                        {
                            sCreated.Remove(sOrder.Dequeue());
                        }

                        sOrder.Enqueue(id.Value);
                    }

                    sCreated[id.Value] = prototypeKey;
                    Creates++;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            return id;
        }

        public void SetYieldingDisabled(bool disabled)
        {
            mInner.SetYieldingDisabled(disabled);
        }

        public ObjectGuid AddObject(object obj, bool postLoad, ObjectGuid objectId)
        {
            return mInner.AddObject(obj, postLoad, objectId);
        }

        public IScriptProxy GetProxy(ObjectGuid objectId)
        {
            return mInner.GetProxy(objectId);
        }

        public IScriptProxy GetProxyPreInit(ObjectGuid objectId)
        {
            return mInner.GetProxyPreInit(objectId);
        }

        public ITask GetTask(ObjectGuid objectId)
        {
            return mInner.GetTask(objectId);
        }

        public void DestroyObject(ObjectGuid scriptHandle)
        {
            try
            {
                if (sCreated.Remove(scriptHandle.Value))
                {
                    Destroys++;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            mInner.DestroyObject(scriptHandle);
        }

        public void DisableScript(ObjectGuid ScriptHandle)
        {
            mInner.DisableScript(ScriptHandle);
        }

        public void Sleep(uint tickCount)
        {
            mInner.Sleep(tickCount);
        }

        public void Wake(ObjectGuid objectId, bool assertIfNotSleeping)
        {
            mInner.Wake(objectId, assertIfNotSleeping);
        }

        public void SetLogicFailureCallback(LogicFailureCallback failurecallback)
        {
            mInner.SetLogicFailureCallback(failurecallback);
        }

        public void SetLogicReplacement(string oldClass, string newClass)
        {
            mInner.SetLogicReplacement(oldClass, newClass);
        }

        public uint CountResources(ResourceKey resKey)
        {
            return mInner.CountResources(resKey);
        }

        public XmlDocument LoadXml(ResourceKey resKey)
        {
            return mInner.LoadXml(resKey);
        }

        public string LoadXmlString(ResourceKey resKey)
        {
            return mInner.LoadXmlString(resKey);
        }

        public XmlDocument LoadNthXml(ResourceKey resKey, uint index)
        {
            return mInner.LoadNthXml(resKey, index);
        }

        public string CreateDnPExportFile(ref uint fileHandle)
        {
            return mInner.CreateDnPExportFile(ref fileHandle);
        }

        public string CreateExportFile(ref uint fileHandle, string prefix)
        {
            return mInner.CreateExportFile(ref fileHandle, prefix);
        }

        public string CreateScriptErrorFile(ref uint fileHandle)
        {
            return mInner.CreateScriptErrorFile(ref fileHandle);
        }

        public bool AppendToScriptErrorFile(uint fileHandle, char[] text)
        {
            return mInner.AppendToScriptErrorFile(fileHandle, text);
        }

        public bool CloseScriptErrorFile(uint fileHandle)
        {
            return mInner.CloseScriptErrorFile(fileHandle);
        }

        public long GetUsedBytes()
        {
            return mInner.GetUsedBytes();
        }

        public ResourceKey[] GetAllSimulationObjectKeys()
        {
            return mInner.GetAllSimulationObjectKeys();
        }

        public string GetSimulationObjectName(ResourceKey key)
        {
            return mInner.GetSimulationObjectName(key);
        }

        public void EnableYieldAnalysis(YieldAnalysisTaskHandler checkTaskHandler, YieldAnalysisMethodHandler checkMethodHandler, YieldAnalysisCallHandler callHandler)
        {
            mInner.EnableYieldAnalysis(checkTaskHandler, checkMethodHandler, callHandler);
        }

        public void DisableYieldAnalysis()
        {
            mInner.DisableYieldAnalysis();
        }

        public string GetObjectStackTrace(ObjectGuid objectHandle)
        {
            return mInner.GetObjectStackTrace(objectHandle);
        }

        public string GetEnumSimpleName(Type enumType, ulong val)
        {
            return mInner.GetEnumSimpleName(enumType, val);
        }

        public uint GetEnumSimpleHash(Type enumType, ulong val)
        {
            return mInner.GetEnumSimpleHash(enumType, val);
        }

        public bool DidSecuritySystemFail()
        {
            return mInner.DidSecuritySystemFail();
        }

        public void ClearReflectionCache()
        {
            mInner.ClearReflectionCache();
        }

        public XmlReader ReadXml(ResourceKey key)
        {
            return mInner.ReadXml(key);
        }

        public XmlReader ReadXml(ResourceKey key, uint index)
        {
            return mInner.ReadXml(key, index);
        }

        public long DoCPPProfile(uint startEndDelta, uint index)
        {
            return mInner.DoCPPProfile(startEndDelta, index);
        }
    }
}

// <copyright file="DeserializedData.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace PerfViewJS
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Diagnostics.Tracing.Etlx;
    using Microsoft.Diagnostics.Tracing.Stacks;

    public sealed class DeserializedData : IDeserializedData
    {
        private readonly string filename;

        private readonly SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);

        private readonly Dictionary<StackViewerModel, ICallTreeData> callTreeDataCache = new Dictionary<StackViewerModel, ICallTreeData>();

        private readonly Dictionary<StackSourceCacheKey, StackSource> stackSourceCache = new Dictionary<StackSourceCacheKey, StackSource>();

        private int initialized;

        private TraceLogDeserializer deserializer;

        private ProcessInfo[] processList;

        private ModuleInfo[] moduleInfoList;

        private StackEventTypeInfo[] stackEventTypesOrderedByName;

        private StackEventTypeInfo[] stackEventTypesOrderedByStackCount;

        private TraceInfo traceInfo;

        public DeserializedData(string filename)
        {
            this.filename = filename;
        }

        public async ValueTask<StackEventTypeInfo[]> GetStackEventTypesAsyncOrderedByName()
        {
            await this.EnsureInitialized();
            return this.stackEventTypesOrderedByName;
        }

        public async ValueTask<StackEventTypeInfo[]> GetStackEventTypesAsyncOrderedByStackCount()
        {
            await this.EnsureInitialized();
            return this.stackEventTypesOrderedByStackCount;
        }

        public async ValueTask<ICallTreeData> GetCallTreeAsync(StackViewerModel model, StackSource stackSource = null)
        {
            await this.EnsureInitialized();

            lock (this.callTreeDataCache)
            {
                if (!this.callTreeDataCache.TryGetValue(model, out var value))
                {
                    double start = string.IsNullOrEmpty(model.Start) ? 0.0 : double.Parse(model.Start);
                    double end = string.IsNullOrEmpty(model.End) ? 0.0 : double.Parse(model.End);

                    var key = new StackSourceCacheKey((ProcessIndex)int.Parse(model.Pid), int.Parse(model.StackType), start, end, model.DrillIntoKey);
                    if (!this.stackSourceCache.TryGetValue(key, out var ss))
                    {
                        ss = stackSource ?? this.deserializer.GetStackSource((ProcessIndex)int.Parse(model.Pid), int.Parse(model.StackType), start, end);
                        this.stackSourceCache.Add(key, ss);
                    }
                    else
                    {
                        var drillIntoStackSource = new CopyStackSource(GetTraceEventStackSource(ss));

                        ss.ForEach(delegate(StackSourceSample sample)
                        {
                            drillIntoStackSource.AddSample(sample);
                        });

                        ss = drillIntoStackSource;
                    }

                    value = new CallTreeData(stackSource ?? ss, model);
                    this.callTreeDataCache.Add(model, value);
                }

                return value;
            }
        }

        public async ValueTask<DetailedProcessInfo> GetDetailedProcessInfoAsync(int processIndex)
        {
            await this.EnsureInitialized();
            return this.deserializer.GetDetailedProcessInfo(processIndex);
        }

        public async ValueTask<ProcessInfo[]> GetProcessChooserAsync()
        {
            await this.EnsureInitialized();
            return this.processList;
        }

        public async ValueTask<List<EventData>> GetEvents(EventViewerModel model)
        {
            await this.EnsureInitialized();
            return this.deserializer.GetEvents(model.EventTypes, model.TextFilter, model.MaxEventCount, model.Start, model.End);
        }

        public async ValueTask<TraceInfo> GetTraceInfoAsync()
        {
            await this.EnsureInitialized();
            return this.traceInfo;
        }

        public async ValueTask<ModuleInfo[]> GetModulesAsync()
        {
            await this.EnsureInitialized();
            return this.moduleInfoList;
        }

        public async ValueTask<string> LookupSymbolAsync(int moduleIndex)
        {
            await this.EnsureInitialized();
            var retVal = this.deserializer.LookupSymbol(moduleIndex);

            lock (this.callTreeDataCache)
            {
                this.callTreeDataCache.Clear();
            }

            return retVal;
        }

        public async ValueTask<string> LookupSymbolsAsync(int[] moduleIndices)
        {
            await this.EnsureInitialized();
            var retVal = this.deserializer.LookupSymbols(moduleIndices);

            lock (this.callTreeDataCache)
            {
                this.callTreeDataCache.Clear();
            }

            return retVal;
        }

        private static TraceEventStackSource GetTraceEventStackSource(StackSource source)
        {
            StackSourceStacks rawSource = source;
            while (true)
            {
                if (rawSource is TraceEventStackSource asTraceEventStackSource)
                {
                    return asTraceEventStackSource;
                }

                if (rawSource is CopyStackSource asCopyStackSource)
                {
                    rawSource = asCopyStackSource.SourceStacks;
                    continue;
                }

                if (rawSource is StackSource asStackSource && asStackSource != asStackSource.BaseStackSource)
                {
                    rawSource = asStackSource.BaseStackSource;
                    continue;
                }

                return null;
            }
        }

        private async Task EnsureInitialized()
        {
            if (Interlocked.CompareExchange(ref this.initialized, 1, comparand: -1) == 0)
            {
                await this.Initialize();
            }
        }

        private async Task Initialize()
        {
            await this.semaphoreSlim.WaitAsync();

            try
            {
                if (this.initialized == 1)
                {
                    return;
                }

                var d = new TraceLogDeserializer(this.filename);
                {
                    var traceProcesses = d.TraceProcesses;
                    var plist = new ProcessInfo[traceProcesses.Count + 1];

                    float totalmsec = 0;
                    int i = 1;

                    foreach (var traceProcess in traceProcesses.OrderByDescending(t => t.CPUMSec))
                    {
                        totalmsec += traceProcess.CPUMSec;
                        plist[i++] = new ProcessInfo(traceProcess.Name + $" ({traceProcess.ProcessID})", (int)traceProcess.ProcessIndex, traceProcess.CPUMSec, traceProcess.ProcessID, traceProcess.ParentID, traceProcess.CommandLine);
                    }

                    plist[0] = new ProcessInfo("All Processes", (int)ProcessIndex.Invalid, totalmsec, -1, -1, string.Empty);

                    this.processList = plist;
                }

                {
                    var eventStats = d.EventStats;

                    var stackEventTypes = new StackEventTypeInfo[eventStats.Count];

                    int i = 0;
                    foreach (var pair in d.EventStats.OrderBy(t => t.Value.FullName))
                    {
                        stackEventTypes[i++] = new StackEventTypeInfo(pair.Key, pair.Value.FullName, pair.Value.Count, pair.Value.StackCount);
                    }

                    this.stackEventTypesOrderedByName = stackEventTypes;
                }

                {
                    var eventStats = d.EventStats;

                    var stackEventTypes = new StackEventTypeInfo[eventStats.Count + 1];

                    stackEventTypes[0] = new StackEventTypeInfo(0, "All Events", d.TotalEventCount, d.TotalStackCount);

                    int i = 1;
                    foreach (var pair in d.EventStats.OrderByDescending(t => t.Value.StackCount))
                    {
                        stackEventTypes[i++] = new StackEventTypeInfo(pair.Key, pair.Value.FullName, pair.Value.Count, pair.Value.StackCount);
                    }

                    this.stackEventTypesOrderedByStackCount = stackEventTypes;
                }

                {
                    var moduleFiles = d.TraceModuleFiles;
                    var moduleInfos = new ModuleInfo[moduleFiles.Count];

                    int index = 0;
                    foreach (var moduleFile in moduleFiles.OrderByDescending(t => t.CodeAddressesInModule))
                    {
                        moduleInfos[index++] = new ModuleInfo((int)moduleFile.ModuleFileIndex, moduleFile.CodeAddressesInModule, moduleFile.FilePath);
                    }

                    this.moduleInfoList = moduleInfos;
                }

                this.traceInfo = d.TraceInfo;
                this.deserializer = d;
                this.initialized = 1;
            }
            finally
            {
                this.semaphoreSlim.Release();
            }
        }

        private readonly struct StackSourceCacheKey : IEquatable<StackSourceCacheKey>
        {
            private const double TOLERANCE = 0.1;

            private readonly ProcessIndex processIndex;

            private readonly int stackType;

            private readonly double start;

            private readonly double end;

            private readonly string drillIntoKey;

            public StackSourceCacheKey(ProcessIndex processIndex, int stackType, double start, double end, string drillIntoKey)
            {
                this.processIndex = processIndex;
                this.stackType = stackType;
                this.start = start;
                this.end = end;
                this.drillIntoKey = drillIntoKey;
            }

            public bool Equals(StackSourceCacheKey other)
            {
                return this.processIndex == other.processIndex && this.stackType == other.stackType && this.drillIntoKey == other.drillIntoKey && Math.Abs(this.start - other.start) < TOLERANCE && Math.Abs(this.end - other.end) < TOLERANCE;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(this.processIndex, this.stackType, this.start, this.end, this.drillIntoKey);
            }
        }
    }
}

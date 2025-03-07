﻿using Microsoft.TeamFoundation.WorkItemTracking.Client;
using Microsoft.VisualStudio.Services.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using VstsSyncMigrator.Engine.ComponentContext;
using VstsSyncMigrator.Engine.Configuration;
using VstsSyncMigrator.Engine.Configuration.FieldMap;
using VstsSyncMigrator.Engine.Configuration.Processing;

namespace VstsSyncMigrator.Engine
{
   public class MigrationEngine
    {
        List<ITfsProcessingContext> processors = new List<ITfsProcessingContext>();
        List<Action<WorkItem, WorkItem>> processorActions = new List<Action<WorkItem, WorkItem>>();
        Dictionary<string, List<IFieldMap>> fieldMapps = new Dictionary<string, List<IFieldMap>>();
        Dictionary<string, IWitdMapper> workItemTypeDefinitions = new Dictionary<string, IWitdMapper>();
        ITeamProjectContext source;
        ITeamProjectContext target;
        VssCredentials sourceCreds;
        VssCredentials targetCreds;
        
        public MigrationEngine()
        {

        }
        public MigrationEngine(EngineConfiguration config)
        {
            ProcessConfiguration(config);
        }

        public MigrationEngine(EngineConfiguration config, VssCredentials sourceCredentials, VssCredentials targetCredentials)
        {
            sourceCreds = sourceCredentials;
            targetCreds = targetCredentials;

            ProcessConfiguration(config);
        }

        private void ProcessConfiguration(EngineConfiguration config)
        {
            Telemetry.EnableTrace = config.TelemetryEnableTrace;
            if (config.Source != null)
            {
                if (sourceCreds == null)
                    SetSource(new TeamProjectContext(config.Source));
                else
                    SetSource(new TeamProjectContext(config.Source, sourceCreds));
            }
            if (config.Target != null)
            {
                if (targetCreds == null)
                    SetTarget(new TeamProjectContext(config.Target));
                else
                    SetTarget(new TeamProjectContext(config.Target, targetCreds));
            }           
            if (config.FieldMaps != null)
            {
                foreach (IFieldMapConfig fieldmapConfig in config.FieldMaps)
                {
                    Trace.WriteLine(string.Format("Adding FieldMap {0}", fieldmapConfig.FieldMap.Name), "MigrationEngine");
                    this.AddFieldMap(fieldmapConfig.WorkItemTypeName, (IFieldMap)Activator.CreateInstance(fieldmapConfig.FieldMap, fieldmapConfig));
                }
            }            
            foreach (string key in config.WorkItemTypeDefinition.Keys)
            {
                Trace.WriteLine(string.Format("Adding Work Item Type {0}", key), "MigrationEngine");
                this.AddWorkItemTypeDefinition(key, new DiscreteWitMapper(config.WorkItemTypeDefinition[key]));
            }
            var enabledProcessors = config.Processors.Where(x => x.Enabled).ToList();
            foreach (ITfsProcessingConfig processorConfig in enabledProcessors)
            {
                if (processorConfig.IsProcessorCompatible(enabledProcessors))
                {
                    Trace.WriteLine($"Adding Processor {processorConfig.Processor.Name}", "MigrationEngine");
                    this.AddProcessor(
                        (ITfsProcessingContext)
                        Activator.CreateInstance(processorConfig.Processor, this, processorConfig));
                }
                else
                {
                    var message = $"[ERROR] Cannot add Processor {processorConfig.Processor.Name}. " +
                                  "Processor is not compatible with other enabled processors in configuration.";
                    Trace.WriteLine(message, "MigrationEngine");
                    throw new InvalidOperationException(message);
                }
            }
        }

        public Dictionary<string, IWitdMapper> WorkItemTypeDefinitions
        {
            get
            {
                return workItemTypeDefinitions;
            }
        }

        public ITeamProjectContext Source
        {
            get
            {
                return source;
            }
        }

        public ITeamProjectContext Target
        {
            get
            {
                return target;
            }
        }


        public ProcessingStatus Run()
        {
            Telemetry.Current.TrackEvent("EngineStart",
                new Dictionary<string, string> {
                    { "Engine", "Migration" }
                },
                new Dictionary<string, double> {
                    { "Processors", processors.Count },
                    { "Actions",  processorActions.Count},
                    { "Mappings", fieldMapps.Count }
                });
            Stopwatch engineTimer = Stopwatch.StartNew();
			ProcessingStatus ps = ProcessingStatus.Complete;
            Trace.WriteLine(string.Format("Beginning run of {0} processors", processors.Count.ToString()), "MigrationEngine");
            foreach (ITfsProcessingContext process in processors)
            {
                Stopwatch processorTimer = Stopwatch.StartNew();
				process.Execute();
                processorTimer.Stop();
                Telemetry.Current.TrackEvent("ProcessorComplete", new Dictionary<string, string> { { "Processor", process.Name }, { "Status", process.Status.ToString() } }, new Dictionary<string, double> { { "ProcessingTime", processorTimer.ElapsedMilliseconds } });
                if (process.Status == ProcessingStatus.Failed)
                {
                    ps = ProcessingStatus.Failed;
                    Trace.WriteLine(string.Format("The Processor {0} entered the failed state...stopping run", process.Name), "MigrationEngine");
                    break;
                }
            }
            engineTimer.Stop();
            Telemetry.Current.TrackEvent("EngineComplete", 
                new Dictionary<string, string> {
                    { "Engine", "Migration" }
                },
                new Dictionary<string, double> {
                    { "EngineTime", engineTimer.ElapsedMilliseconds }
                });
            return ps;
        }

        public void AddProcessor<TProcessor>()
        {
            ITfsProcessingContext pc = (ITfsProcessingContext)Activator.CreateInstance(typeof(TProcessor), new object[] { this });
            AddProcessor(pc);
        }

        public void AddProcessor(ITfsProcessingContext processor)
        {
            processors.Add(processor);
        }

        public void SetSource(ITeamProjectContext teamProjectContext)
        {
            source = teamProjectContext;
        }

        public void SetTarget(ITeamProjectContext teamProjectContext)
        {
            target = teamProjectContext;
        }

        public void AddFieldMap(string workItemTypeName, IFieldMap fieldToTagFieldMap)
        {
            if (!fieldMapps.ContainsKey(workItemTypeName))
            {
                fieldMapps.Add(workItemTypeName, new List<IFieldMap>());
            }
            fieldMapps[workItemTypeName].Add(fieldToTagFieldMap);
        }
         public void AddWorkItemTypeDefinition(string workItemTypeName, IWitdMapper workItemTypeDefinitionMap = null)
        {
            if (!workItemTypeDefinitions.ContainsKey(workItemTypeName))
            {
                workItemTypeDefinitions.Add(workItemTypeName, workItemTypeDefinitionMap);
            }
        }

        internal void ApplyFieldMappings(WorkItem source, WorkItem target)
        { 
            if (fieldMapps.ContainsKey("*"))
            {
                ProcessFieldMapList(source, target, fieldMapps["*"]);
            }
            if (fieldMapps.ContainsKey(source.Type.Name))
            {
                ProcessFieldMapList(source, target, fieldMapps[source.Type.Name]);
            }
        }

        internal void ApplyFieldMappings(WorkItem target)
        {
            if (fieldMapps.ContainsKey("*"))
            {
                ProcessFieldMapList(target, target, fieldMapps["*"]);
            }
            if (fieldMapps.ContainsKey(target.Type.Name))
            {
                ProcessFieldMapList(target, target, fieldMapps[target.Type.Name]);
            }
        }

        private  void ProcessFieldMapList(WorkItem source, WorkItem target, List<IFieldMap> list)
        {
            foreach (IFieldMap map in list)
            {
                Trace.WriteLine(string.Format("Running Field Map: {0} {1}", map.Name, map.MappingDisplayName));
                map.Execute(source, target);
            }
        }

    }
}

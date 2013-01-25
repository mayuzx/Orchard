﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json;
using Orchard.ContentManagement;
using Orchard.Forms.Services;
using Orchard.Logging;
using Orchard.Workflows.Models;
using Orchard.Data;
using Orchard.Localization;
using Orchard.Tokens;
using Orchard.Workflows.Models.Descriptors;

namespace Orchard.Workflows.Services {
    public class WorkflowManager : IWorkflowManager {
        private readonly IActivitiesManager _activitiesManager;
        private readonly IRepository<ActivityRecord> _activityRepository;
        private readonly IRepository<WorkflowRecord> _workflowRepository;
        private readonly IRepository<AwaitingActivityRecord> _awaitingActivityRepository;
        private readonly IRepository<WorkflowDefinitionRecord> _workflowDefinitionRepository;
        private readonly ITokenizer _tokenizer;

        public WorkflowManager(
            IActivitiesManager activitiesManager,
            IRepository<ActivityRecord> activityRepository,
            IRepository<WorkflowRecord> workflowRepository,
            IRepository<AwaitingActivityRecord> awaitingActivityRepository,
            IRepository<WorkflowDefinitionRecord> workflowDefinitionRepository,
            ITokenizer tokenizer) {
            _activitiesManager = activitiesManager;
            _activityRepository = activityRepository;
            _workflowRepository = workflowRepository;
            _awaitingActivityRepository = awaitingActivityRepository;
            _workflowDefinitionRepository = workflowDefinitionRepository;
            _tokenizer = tokenizer;

            Logger = NullLogger.Instance;
        }

        public Localizer T { get; set; }
        public ILogger Logger { get; set; }

        public void TriggerEvent(string name, IContent target, Func<Dictionary<string, object>> tokensContext) {
            var tokens = tokensContext();

            var activity = _activitiesManager.GetActivityByName(name);

            if (activity == null) {
                Logger.Error("Activity {0} was not found", name);
                return;
            }
            
            var startedWorkflows = new List<ActivityRecord>();

            // look for workflow definitions with a corresponding starting activity
            // it's important to return activities at this point and not workflows,
            // as a workflow definition could have multiple entry points with the same type of activity
            startedWorkflows.AddRange(_activityRepository.Table.Where(
                x =>x.Name == name && x.Start && x.WorkflowDefinitionRecord.Enabled
                )
            );

            var awaitingActivities = new List<AwaitingActivityRecord>();

            // and any running workflow paused on this kind of activity for this content
            // it's important to return activities at this point as a workflow could be awaiting 
            // on several ones. When an activity is restarted, all the other ones of the same workflow are cancelled.
            awaitingActivities.AddRange(_awaitingActivityRepository.Table.Where(
                x => x.ActivityRecord.Name == name && x.ActivityRecord.Start == false && x.ContentItemRecord == target.ContentItem.Record
                ).ToList()
            );

            // if no activity record is matching the event, do nothing
            if (!startedWorkflows.Any() && !awaitingActivities.Any()) {
                return;
            }

            // evaluate processing condition
            awaitingActivities = awaitingActivities.Where(a => {
                var formatted = JsonConvert.DeserializeXNode(a.ActivityRecord.State, "Root").ToString();
                var tokenized = _tokenizer.Replace(formatted, tokens);
                var serialized = String.IsNullOrEmpty(tokenized) ? "{}" : JsonConvert.SerializeXNode(XElement.Parse(tokenized));
                var state = FormParametersHelper.FromJsonString(serialized);
                var workflowState = FormParametersHelper.FromJsonString(a.WorkflowRecord.State);
                var context = new ActivityContext { Tokens = tokens, State = state.Root, WorkflowState = workflowState, Content = target };

                // check the condition
                try {
                    return activity.CanExecute(context);
                }
                catch (Exception e) {
                    Logger.Error("Error while evaluating an activity condition on {0}: {1}", name, e.ToString());
                    return false;
                }
            }).ToList();

            // evaluate processing condition
            startedWorkflows = startedWorkflows.Where(a => {
                var formatted = JsonConvert.DeserializeXNode(a.State, "Root").ToString();
                var tokenized = _tokenizer.Replace(formatted, tokens);
                var serialized = String.IsNullOrEmpty(tokenized) ? "{}" : JsonConvert.SerializeXNode(XElement.Parse(tokenized));
                var state = FormParametersHelper.FromJsonString(serialized);
                var workflowState = FormParametersHelper.FromJsonString("{}");
                var context = new ActivityContext { Tokens = tokens, State = state.Root, WorkflowState = workflowState, Content = target };

                // check the condition
                try {
                    return activity.CanExecute(context);
                }
                catch (Exception e) {
                    Logger.Error("Error while evaluating an activity condition on {0}: {1}", name, e.ToString());
                    return false;
                }
            }).ToList();

            // if no activity record is matching the event, do nothing
            if (!startedWorkflows.Any() && !awaitingActivities.Any()) {
                return;
            }

            // load workflow definitions too for eager loading
            _workflowDefinitionRepository.Table
                .Where(x => x.Enabled && x.ActivityRecords.Any(e => e.Name == name))
                .ToList();

            // resume halted workflows
            foreach (var a in awaitingActivities) {
                ResumeWorkflow(a, target, tokens);
            }

            // start new workflows
            foreach (var a in startedWorkflows) {
                StartWorkflow(a, target, tokens);
            }
        }

        private void StartWorkflow(ActivityRecord activityRecord, IContent target, Dictionary<string, object> tokens) {
            var workflowState = FormParametersHelper.FromJsonString("{}");
            IEnumerable<ActivityRecord> blockedOn = ExecuteWorkflow(activityRecord.WorkflowDefinitionRecord, activityRecord, target, tokens, workflowState);

            // is the workflow halted on a blocking activity ?
            if (blockedOn == null || !blockedOn.Any()) {
                // no, nothing to do
            }
            else {
                // workflow halted, create a workflow state
                var workflow = new WorkflowRecord {
                    WorkflowDefinitionRecord = activityRecord.WorkflowDefinitionRecord,
                    State = FormParametersHelper.ToJsonString(workflowState)
                };

                _workflowRepository.Create(workflow);

                foreach (var blocking in blockedOn) {
                    workflow.AwaitingActivities.Add(new AwaitingActivityRecord {
                        ActivityRecord = blocking,
                        ContentItemRecord = target.ContentItem.Record
                    });
                }
            }
        }

        private void ResumeWorkflow(AwaitingActivityRecord awaitingActivityRecord, IContent target, Dictionary<string, object> tokens) {
            var workflowState = FormParametersHelper.FromJsonString(awaitingActivityRecord.WorkflowRecord.State);
            IEnumerable<ActivityRecord> blockedOn = ExecuteWorkflow(awaitingActivityRecord.WorkflowRecord.WorkflowDefinitionRecord, awaitingActivityRecord.ActivityRecord, target, tokens, workflowState);

            // is the workflow halted on a blocking activity ?
            if (blockedOn == null || !blockedOn.Any()) {
                // no, delete the workflow
                _workflowRepository.Delete(awaitingActivityRecord.WorkflowRecord);
            }
            else {
                // remove all previous awaiting activities
                var workflow = awaitingActivityRecord.WorkflowRecord;
                workflow.State = FormParametersHelper.ToJsonString(workflowState);
                workflow.AwaitingActivities.Clear();

                // add the new ones
                foreach (var blocking in blockedOn) {
                    workflow.AwaitingActivities.Add(new AwaitingActivityRecord {
                        ActivityRecord = blocking,
                        ContentItemRecord = target.ContentItem.Record
                    });
                }
            }
        }

        public IEnumerable<ActivityRecord> ExecuteWorkflow(WorkflowDefinitionRecord workflowDefinitionRecord, ActivityRecord activityRecord, IContent target, Dictionary<string, object> tokens, dynamic workflowState) {
            var firstPass = true;
            var pending = new Stack<ActivityRecord>();
            pending.Push(activityRecord);

            var blocking = new List<ActivityRecord>();

            while (pending.Any()) {
                
                activityRecord = pending.Pop();

                // while there is an activity to process
                var activity = _activitiesManager.GetActivityByName(activityRecord.Name);

                if (!firstPass){
                    if(activity.IsEvent) {
                        activity.Touch(workflowState);
                        blocking.Add(activityRecord);
                        continue;
                    }
                }
                else {
                    firstPass = false;    
                }

                var state = FormParametersHelper.FromJsonString(activityRecord.State);
                var activityContext = new ActivityContext { Tokens = tokens, State = state, WorkflowState = workflowState, Content = target };
                var outcomes = activity.Execute(activityContext);

                if (outcomes != null) {
                    foreach (var outcome in outcomes) {
                        // look for next activity in the graph
                        var transition = workflowDefinitionRecord.TransitionRecords.FirstOrDefault(x => x.SourceActivityRecord == activityRecord && x.SourceEndpoint == outcome.TextHint);

                        if (transition != null) {
                            pending.Push(transition.DestinationActivityRecord);
                        }
                    }
                }
            }

            // apply Distinct() as two paths could block on the same activity
            return blocking.Distinct();
        }
    }
}
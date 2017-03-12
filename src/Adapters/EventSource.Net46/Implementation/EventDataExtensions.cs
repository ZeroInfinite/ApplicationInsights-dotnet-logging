﻿//-----------------------------------------------------------------------
// <copyright file="EventDataExtensions.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.ApplicationInsights.EventSourceListener.Implementation
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.Tracing;
    using System.Globalization;
    using System.Linq;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.TraceEvent.Shared.Utilities;

    internal static class EventDataExtensions
    {
        private static Lazy<Random> random = new Lazy<Random>();

        private static SeverityLevel[] eventLevelToSeverityLevel = new SeverityLevel[]
        {
            SeverityLevel.Critical,     // EventLevel.LogAlways == 0
            SeverityLevel.Critical,     // EventLevel.Critical == 1
            SeverityLevel.Error,        // EventLevel.Error == 2
            SeverityLevel.Warning,      // EventLevel.Warning == 3
            SeverityLevel.Information,  // EventLevel.Informational == 4
            SeverityLevel.Verbose       // EventLevel.Verbose == 5
        };

        /// <summary>
        /// Creates a TraceTelemetry out of an EventSource event and tracks it using the supplied client.
        /// </summary>
        /// <param name="eventSourceEvent">The source for the telemetry data.</param>
        /// <param name="client">Client to track the data with.</param>
        public static void Track(this EventWrittenEventArgs eventSourceEvent, TelemetryClient client)
        {
            Debug.Assert(client != null, "Should always receive a valid client");

            string formattedMessage = null;
            if (eventSourceEvent.Message != null)
            {
                try
                {
                    // If the event has a badly formatted manifest, message formatting might fail
                    formattedMessage = string.Format(CultureInfo.InvariantCulture, eventSourceEvent.Message, eventSourceEvent.Payload.ToArray());
                }
                catch
                {
                }
            }
            TraceTelemetry telemetry = new TraceTelemetry(formattedMessage, eventLevelToSeverityLevel[(int)eventSourceEvent.Level]);

            eventSourceEvent.ExtractPayloadData(telemetry);

            telemetry.AddProperty(nameof(EventWrittenEventArgs.EventId), eventSourceEvent.EventId.ToString(CultureInfo.InvariantCulture));
            telemetry.AddProperty(nameof(EventWrittenEventArgs.EventName), eventSourceEvent.EventName);
            if (eventSourceEvent.ActivityId != default(Guid))
            {
                telemetry.AddProperty(nameof(EventWrittenEventArgs.ActivityId), ActivityPathDecoder.GetActivityPathString(eventSourceEvent.ActivityId));
            }
            if (eventSourceEvent.RelatedActivityId != default(Guid))
            {
                telemetry.AddProperty(nameof(EventWrittenEventArgs.RelatedActivityId), eventSourceEvent.RelatedActivityId.ToString());
            }
            telemetry.AddProperty(nameof(EventWrittenEventArgs.Channel), eventSourceEvent.Channel.GetChannelName());
            telemetry.AddProperty(nameof(EventWrittenEventArgs.Keywords), GetHexRepresentation((long)eventSourceEvent.Keywords));
            telemetry.AddProperty(nameof(EventWrittenEventArgs.Opcode), eventSourceEvent.Opcode.GetOpcodeName());
            if (eventSourceEvent.Tags != EventTags.None)
            {
                telemetry.AddProperty(nameof(EventWrittenEventArgs.Tags), GetHexRepresentation((int)eventSourceEvent.Tags));
            }
            if (eventSourceEvent.Task != EventTask.None)
            {
                telemetry.AddProperty(nameof(EventWrittenEventArgs.Task), GetHexRepresentation((int)eventSourceEvent.Task));
            }

            client.Track(telemetry);
        }

        /// <summary>
        /// Extracts payload properties from a given EventSource event and populates the telemetry properties with values found.
        /// </summary>
        /// <param name="eventSourceEvent">Event to extract values from.</param>
        /// <param name="telemetry">Telemetry item to populate with properties.</param>
        private static void ExtractPayloadData(this EventWrittenEventArgs eventSourceEvent, TraceTelemetry telemetry)
        {
            Debug.Assert(telemetry != null, "Should have received a valid TraceTelemetry object");

            if (eventSourceEvent.Payload == null || eventSourceEvent.PayloadNames == null)
            {
                return;
            }

            IDictionary<string, string> payloadData = telemetry.Properties;

            IEnumerator<object> payloadEnumerator = eventSourceEvent.Payload.GetEnumerator();
            IEnumerator<string> payloadNamesEnunmerator = eventSourceEvent.PayloadNames.GetEnumerator();
            while (payloadEnumerator.MoveNext())
            {
                payloadNamesEnunmerator.MoveNext();
                if (payloadEnumerator.Current != null)
                {
                    payloadData.Add(payloadNamesEnunmerator.Current, payloadEnumerator.Current.ToString());
                }
            }
        }

        /// <summary>
        /// Adds a property to a telemetry item.
        /// </summary>
        /// <param name="telemetry">Telemetry item that receives a new property.</param>
        /// <param name="name">Property name.</param>
        /// <param name="value">Property value.</param>
        /// <remarks>There is a potential of naming conflicts between standard ETW properties (like Keywords, Channel)
        /// and properties that are part of EventSource event payload. Because both end up in the same ITelemetry.Properties dictionary,
        /// we need some sort of conflict resolution. In this implementation we err on the side of preserving names that are part of EventSource event payload
        /// because they are usually the "interesting" properties, specific to the application. If there is a conflict with standard properties,
        /// we make the standard property name unique by appending a random numeric suffix.</remarks>
        private static void AddProperty(this TraceTelemetry telemetry, string name, string value)
        {
            Debug.Assert(!string.IsNullOrEmpty(name), "Property name should always be specified");

            IDictionary<string, string> properties = telemetry.Properties;
            if (!properties.ContainsKey(name))
            {
                properties.Add(name, value);
                return;
            }

            string newKey = name + "_";

            // Update property key till there is no such key in dict
            do
            {
                newKey += EventDataExtensions.random.Value.Next(0, 10);
            }
            while (properties.ContainsKey(newKey));

            properties.Add(newKey, value);
        }

        /// <summary>
        /// Returns a string representation of an EventChannel.
        /// </summary>
        /// <param name="channel">The channel to get a name for.</param>
        /// <returns>Name of the channel (or a numeric string, if standard name is not known).</returns>
        /// <remarks>Enum.GetName() could be used but it is using reflection and because of that it is an order of magnitude less efficient.</remarks>
        private static string GetChannelName(this EventChannel channel)
        {
            switch (channel)
            {
                case EventChannel.None: return nameof(EventChannel.None);
                case EventChannel.Admin: return nameof(EventChannel.Admin);
                case EventChannel.Operational: return nameof(EventChannel.Operational);
                case EventChannel.Analytic: return nameof(EventChannel.Analytic);
                case EventChannel.Debug: return nameof(EventChannel.Debug);
                default: return channel.ToString();
            }
        }

        /// <summary>
        /// Returns a string representation of an operation code.
        /// </summary>
        /// <param name="opcode">The operation code to get a name for.</param>
        /// <returns>Name of the operation code (or a numeric string, if standard name is not known).</returns>
        /// <remarks>Enum.GetName() could be used but it is using reflection and because of that it is an order of magnitude less efficient.</remarks>
        private static string GetOpcodeName(this EventOpcode opcode)
        {
            switch (opcode)
            {
                case EventOpcode.Info: return nameof(EventOpcode.Info);
                case EventOpcode.Start: return nameof(EventOpcode.Start);
                case EventOpcode.Stop: return nameof(EventOpcode.Stop);
                case EventOpcode.DataCollectionStart: return nameof(EventOpcode.DataCollectionStart);
                case EventOpcode.DataCollectionStop: return nameof(EventOpcode.DataCollectionStop);
                case EventOpcode.Extension: return nameof(EventOpcode.Extension);
                case EventOpcode.Reply: return nameof(EventOpcode.Reply);
                case EventOpcode.Resume: return nameof(EventOpcode.Resume);
                case EventOpcode.Suspend: return nameof(EventOpcode.Suspend);
                case EventOpcode.Send: return nameof(EventOpcode.Send);
                case EventOpcode.Receive: return nameof(EventOpcode.Receive);
                default: return opcode.ToString();
            }
        }

        private static string GetHexRepresentation(long l)
        {
            return "0x" + l.ToString("X16", CultureInfo.InvariantCulture);
        }

        private static string GetHexRepresentation(int i)
        {
            return "0x" + i.ToString("X8", CultureInfo.InvariantCulture);
        }
    }
}

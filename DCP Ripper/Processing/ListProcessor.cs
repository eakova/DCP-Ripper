﻿using DCP_Ripper.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DCP_Ripper.Processing {
    /// <summary>
    /// Processes a list of compositions.
    /// </summary>
    public class ListProcessor {
        /// <summary>
        /// Marks the content parent folder for output path.
        /// </summary>
        public const string parentMarker = "parent";

        /// <summary>
        /// Marks if there's a running conversion.
        /// </summary>
        public bool InProgress => task != null && !task.IsCompleted;

        /// <summary>
        /// The list of compositions to process.
        /// </summary>
        public List<CompositionInfo> Compositions { get; set; }

        /// <summary>
        /// Launch location of ffmpeg.exe.
        /// </summary>
        public string FFmpegPath { get; set; } = null;

        /// <summary>
        /// Forced content output path. Null means default (next to video files), <see cref="parentMarker"/> means its parent.
        /// </summary>
        public string OutputPath { get; set; } = null;

        /// <summary>
        /// Process state update.
        /// </summary>
        /// <param name="status">Current job</param>
        public delegate void StatusUpdate(string status);

        /// <summary>
        /// Called when a new job is started.
        /// </summary>
        public event StatusUpdate OnStatusUpdate;

        /// <summary>
        /// Called when list processing is finished.
        /// </summary>
        public event Action OnCompletion;

        /// <summary>
        /// List of failed content.
        /// </summary>
        StringBuilder failures;

        /// <summary>
        /// Async process handler.
        /// </summary>
        Task task;

        /// <summary>
        /// Process a single composition.
        /// </summary>
        void ProcessSingle(CompositionInfo composition) {
            if (!File.Exists(composition.Path)) {
                failures.AppendLine(Path.GetFileName(composition.Path) + " does not exist.");
                return;
            }
            OnStatusUpdate?.Invoke($"Processing {composition}...");
            string finalOutput = OutputPath,
                sourceFolder = Path.GetDirectoryName(composition.Path);
            if (!string.IsNullOrEmpty(OutputPath) && OutputPath.Equals(parentMarker)) {
                finalOutput = Path.GetDirectoryName(sourceFolder);
                if (finalOutput == null) {
                    failures.AppendLine($"Can't output {composition} above a root folder.");
                    return;
                }
            }

            CompositionProcessor processor = new(FFmpegPath, composition) {
                ForcePath = finalOutput
            };
            if (processor.ProcessComposition()) {
                if (Settings.Default.zipAfter) {
                    OnStatusUpdate?.Invoke($"Zipping {composition}...");
                    Finder.ZipAssets(sourceFolder, $"{finalOutput}\\{composition.StandardTitle}.zip",
                        textOut => OnStatusUpdate(textOut));
                }
                if (Settings.Default.deleteAftter)
                    Finder.DeleteAssets(sourceFolder);
            } else
                failures.AppendLine($"Conversion of {composition} failed - most likely a codec error.");
        }

        /// <summary>
        /// Start processing the compositions.
        /// </summary>
        /// <returns>Number of successful conversions</returns>
        public void Process() {
            failures = new StringBuilder();
            foreach (CompositionInfo composition in Compositions)
                ProcessSingle(composition);
            OnStatusUpdate?.Invoke("Finished!");
            OnCompletion?.Invoke();
        }

        /// <summary>
        /// Start processing the selected composition.
        /// </summary>
        /// <returns>Number of successful conversions</returns>
        public void ProcessSelected(List<CompositionInfo> compositions) {
            failures = new StringBuilder();
            foreach (CompositionInfo composition in compositions)
                ProcessSingle(composition);
            OnStatusUpdate?.Invoke("Finished!");
            OnCompletion?.Invoke();
        }

        /// <summary>
        /// Start processing the compositions as a <see cref="Task"/>.
        /// </summary>
        public Task ProcessAsync() {
            if (InProgress)
                return task;
            task = new Task(Process);
            task.Start();
            return task;
        }

        /// <summary>
        /// Start processing the selected compositions as a <see cref="Task"/>.
        /// </summary>
        public Task ProcessSelectedAsync(List<CompositionInfo> compositions) {
            if (InProgress)
                return task;
            task = new Task(() => ProcessSelected(compositions));
            task.Start();
            return task;
        }

        /// <summary>
        /// Gets a string where each line is a failed content that could not be processed.
        /// </summary>
        public string GetFailedContents() => failures.ToString();
    }
}
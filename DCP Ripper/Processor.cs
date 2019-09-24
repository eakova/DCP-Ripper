﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Xml;

namespace DCP_Ripper {
    /// <summary>
    /// Rips a composition.
    /// </summary>
    public class Processor {
        /// <summary>
        /// Composition title.
        /// </summary>
        public string Title { get; private set; }

        /// <summary>
        /// This composition is 4K.
        /// </summary>
        public bool Is4K { get; private set; }

        /// <summary>
        /// Video codec name for FFmpeg.
        /// </summary>
        public string VideoFormat { get; set; } = "libx265";

        /// <summary>
        /// Use chroma subsampling.
        /// </summary>
        public bool ChromaSubsampling {
            get => VideoFormat.Equals("libx264") ? true : chromaSubsampling;
            set => chromaSubsampling = value;
        }

        /// <summary>
        /// Constant Rate Factor for AVC/HEVC codecs.
        /// </summary>
        public int CRF { get; set; } = 23;

        /// <summary>
        /// Constant Rate Factor for AVC/HEVC codecs when ripping 3D content.
        /// </summary>
        public int CRF3D { get; set; } = 18;

        /// <summary>
        /// Audio codec name for FFmpeg.
        /// </summary>
        public string AudioFormat { get; set; } = "libopus";

        /// <summary>
        /// List of reel data in this composition.
        /// </summary>
        public IReadOnlyList<Content> Contents => contents;

        /// <summary>
        /// Use chroma subsampling.
        /// </summary>
        bool chromaSubsampling = false;

        /// <summary>
        /// List of reel data in this composition.
        /// </summary>
        readonly List<Content> contents = new List<Content>();

        /// <summary>
        /// Path of FFmpeg.
        /// </summary>
        readonly string ffmpegPath;

        /// <summary>
        /// Get the file names for UUIDs in a given composition.
        /// </summary>
        Dictionary<string, string> ParseAssetMap(string directory) {
            Dictionary<string, string> map = new Dictionary<string, string>();
            string nextId = string.Empty;
            using (XmlReader reader = XmlReader.Create(directory + "ASSETMAP")) {
                while (reader.Read()) {
                    if (reader.NodeType != XmlNodeType.Element)
                        continue;
                    switch (reader.Name) {
                        case "Id":
                            reader.Read();
                            nextId = reader.Value;
                            break;
                        case "Path":
                            reader.Read();
                            map.Add(nextId, reader.Value);
                            break;
                        default:
                            break;
                    }
                }
            }
            return map;
        }

        /// <summary>
        /// Load a composition for processing.
        /// </summary>
        public Processor(string ffmpegPath, string cplPath) {
            this.ffmpegPath = ffmpegPath;
            string directory = cplPath.Substring(0, cplPath.LastIndexOf('\\') + 1);
            Dictionary<string, string> assets = ParseAssetMap(directory);
            Content reel = new Content();
            using (XmlReader reader = XmlReader.Create(cplPath)) {
                bool video = true;
                while (reader.Read()) {
                    if (reader.NodeType != XmlNodeType.Element) {
                        if (reader.NodeType == XmlNodeType.EndElement && reader.Name.Equals("Reel"))
                            contents.Add(reel);
                        continue;
                    } else if (reader.Name.EndsWith("MainStereoscopicPicture")) {
                        video = true;
                        reel.is3D = true;
                        continue;
                    }
                    switch (reader.Name) {
                        case "Id":
                            reader.Read();
                            if (assets.ContainsKey(reader.Value)) {
                                if (video)
                                    reel.videoFile = directory + assets[reader.Value];
                                else
                                    reel.audioFile = directory + assets[reader.Value];
                            }
                            break;
                        case "EntryPoint":
                            reader.Read();
                            if (video)
                                reel.videoStartFrame = int.Parse(reader.Value);
                            else
                                reel.audioStartFrame = int.Parse(reader.Value);
                            break;
                        case "Duration":
                            reader.Read();
                            reel.duration = int.Parse(reader.Value);
                            break;
                        case "FrameRate":
                            reader.Read();
                            reel.framerate = int.Parse(reader.Value.Substring(0, reader.Value.IndexOf(' ')));
                            break;
                        case "Reel":
                            reel = new Content();
                            break;
                        case "MainPicture":
                            video = true;
                            break;
                        case "MainSound":
                            video = false;
                            break;
                        case "MainSubtitle":
                            while (reader.Read() && (reader.NodeType != XmlNodeType.EndElement || !reader.Name.Equals("MainSubtitle"))) ;
                            break;
                        case "ContentTitleText":
                            reader.Read();
                            Is4K = (Title = reader.Value).Contains("_4K");
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Launch the FFmpeg to process a file with the given arguments.
        /// </summary>
        bool LaunchFFmpeg(string arguments) {
            ProcessStartInfo start = new ProcessStartInfo {
                Arguments = arguments,
                FileName = ffmpegPath
            };
            try {
                using (Process proc = Process.Start(start)) {
                    proc.WaitForExit();
                    return proc.ExitCode == 0;
                }
            } catch {
                return false;
            }
        }

        /// <summary>
        /// Process a video file. The created file will have the same name, but in Matroska format, which is the returned value.
        /// </summary>
        public string ProcessVideo(Content content, string extraModifiers = "") {
            string videoStart = (content.videoStartFrame / (float)content.framerate).ToString("0.000").Replace(',', '.');
            string subsampling = ChromaSubsampling ? "-pix_fmt yuv420p" : string.Empty;
            string fileName = content.videoFile.Replace(".mxf", ".mkv").Replace(".MXF", ".mkv");
#if DEBUG
            if (File.Exists(fileName))
                return fileName;
#endif
            if (!content.is3D) {
                string length = (content.duration / (float)content.framerate).ToString("0.000").Replace(',', '.');
                return LaunchFFmpeg(string.Format("-ss {0} -i \"{1}\" -t {2} -c:v {3} {4} {5} -crf {6} -v error -stats \"{7}\"",
                    videoStart, content.videoFile, length, VideoFormat, subsampling, extraModifiers, CRF, fileName)) ? fileName : null;
            }
            string doubleRate = "-r " + content.framerate;
            string leftFile = content.videoFile.Replace(".mxf", "_L.mkv").Replace(".MXF", "_L.mkv");
            string doubleLength = "-t " + (content.duration * 2 / (float)content.framerate).ToString("0.000").Replace(',', '.');
            string lowerCRF = "-crf " + Math.Max(CRF3D - 5, 0);
#if DEBUG
            if (!File.Exists(leftFile))
#endif
            if (!LaunchFFmpeg(string.Format("{0} -ss {1} -i \"{2}\" {3} -vf select=\"mod(n-1\\,2)\",scale=iw/2:ih,setsar=1:1 " +
                "-c:v {4} {5} -v error -stats \"{6}\"",
                doubleRate, videoStart, content.videoFile, doubleLength, VideoFormat, lowerCRF, leftFile)))
                return null;
            string rightFile = content.videoFile.Replace(".mxf", "_R.mkv").Replace(".MXF", "_R.mkv");
#if DEBUG
            if (!File.Exists(rightFile))
#endif
            if (!LaunchFFmpeg(string.Format("{0} -ss {1} -i \"{2}\" {3} -vf select=\"not(mod(n-1\\,2))\",scale=iw/2:ih,setsar=1:1 " +
                "-c:v {4} {5} -v error -stats \"{6}\"",
                doubleRate, videoStart, content.videoFile, doubleLength, VideoFormat, lowerCRF, rightFile))) {
                return null;
            }
            if (LaunchFFmpeg(string.Format("-i \"{0}\" -i \"{1}\" -filter_complex [0:v][1:v]hstack=inputs=2[v] -map [v] " +
                "-c:v {2} -crf {3} -v error -stats \"{4}\"",
                leftFile, rightFile, VideoFormat, CRF3D, fileName))) {
                File.Delete(leftFile);
                File.Delete(rightFile);
                return fileName;
            }
            return null;
        }

        /// <summary>
        /// Process a video file. If the resolution is 4K, it will be downscaled to 2K.
        /// The created file will have the same name, but in Matroska format, which is the returned value.
        /// </summary>
        public string ProcessVideo2K(Content content) => ProcessVideo(content, Is4K ? "-vf scale=iw/2:ih/2" : string.Empty);

        /// <summary>
        /// Process the audio file of a content. The created file will have the same name, but in Matroska format, which is the returned value.
        /// </summary>
        public string ProcessAudio(Content content) {
            string fileName = content.audioFile.Replace(".mxf", ".mkv").Replace(".MXF", ".mkv");
#if DEBUG
            if (File.Exists(fileName))
                return fileName;
#endif
            return LaunchFFmpeg(string.Format("-i \"{0}\" -ss {1} -t {2} -c:a {3} -v error -stats \"{4}\"",
                content.audioFile,
                (content.audioStartFrame / (float)content.framerate).ToString("0.000").Replace(',', '.'),
                (content.duration / (float)content.framerate).ToString("0.000").Replace(',', '.'),
                AudioFormat,
                fileName)) ? fileName : null;
        }

        /// <summary>
        /// Merge a converted video and audio file, deleting the sources.
        /// </summary>
        public bool Merge(string video, string audio, string fileName) {
            LaunchFFmpeg(string.Format("-i \"{0}\" -i \"{1}\" -c copy -v error -stats \"{2}\"", video, audio, fileName));
            if (File.Exists(video) && File.Exists(audio)) {
                File.Delete(video);
                File.Delete(audio);
                return File.Exists(fileName);
            }
            return false;
        }

        /// <summary>
        /// Process the video files of this DCP. Returns if all reels were successfully processed.
        /// </summary>
        /// <param name="force2K">Downscale 4K content to 2K</param>
        /// <param name="forcePath">Change the default output directory, which is the container of the video file</param>
        public bool ProcessComposition(bool force2K = false, string forcePath = null) {
            int reelsDone = 0;
            for (int i = 0, length = contents.Count; i < length; ++i) {
                string video = force2K ? ProcessVideo2K(contents[i]) : ProcessVideo(contents[i]);
                string audio = ProcessAudio(contents[i]);
                if (video != null && audio != null) {
                    string path = forcePath;
                    if (path == null)
                        path = contents[i].videoFile.Substring(0, contents[i].videoFile.LastIndexOf("\\") + 1);
                    else if (!path.EndsWith("\\"))
                        path += '\\';
                    string fileName = length == 1 ? Title + ".mkv" : string.Format("{0}_{1}.mkv",
                        force2K ? Title.Replace("_4K", "_2K") : Title, i + 1);
                    if (Merge(video, audio, path + fileName))
                        ++reelsDone;
                }
            }
            return reelsDone == contents.Count;
        }
    }
}
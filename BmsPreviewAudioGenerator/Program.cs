﻿using BmsPreviewAudioGenerator.MixEvent;
using ManagedBass;
using ManagedBass.Enc;
using ManagedBass.Mix;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace BmsPreviewAudioGenerator
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine($"Program version:{typeof(Program).Assembly.GetName().Version}");

            var result = Bass.Init();

            var st = CommandLine.TryGetOptionValue<string>("start", out var s) ? s : null;
            var et = CommandLine.TryGetOptionValue<string>("end", out var e) ? e : null;
            var sn = CommandLine.TryGetOptionValue<string>("save_name", out var sw) ? sw : null;
            var path = CommandLine.TryGetOptionValue<string>("path", out var p) ? p : throw new Exception("MUST type a path.");
            var bms = CommandLine.TryGetOptionValue<string>("bms", out var b) ? b : null;
            var batch = CommandLine.ContainSwitchOption("batch");
            var fc = CommandLine.TryGetOptionValue<bool>("fast_clip", out var ww) ? ww : false;

            if (batch && !string.IsNullOrWhiteSpace(bms))
                throw new Exception("Not allow set param \"bms\" and \"batch\" at same time!");

            var target_directories = batch ? EnumerateConvertableDirectories(path) : new[] { path };

            for (int i = 0; i < target_directories.Length; i++)
            {
                Console.WriteLine($"-------\t{i + 1}/{target_directories.Length} ({100.0f * (i + 1) / target_directories.Length:F2}%)\t-------");
                var dir = target_directories[i];
                try
                {
                    GeneratePreviewAudio(dir, bms, st, et, save_file_name: sn,fast_clip:fc);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed.\n{ex.Message}\n{ex.StackTrace}");
                }

#if DEBUG
                Debug.Assert(Bass.LastError == Errors.OK, $"Bass.LastError = {Bass.LastError}");
#endif
            }
        }

        private static string[] EnumerateConvertableDirectories(string path)
        {
            var result = Directory.EnumerateFiles(path, "*.bms", SearchOption.AllDirectories).Select(x => Path.GetDirectoryName(x)).Distinct().ToArray();

            return result;
        }

        /// <summary>
        /// 切鸡鸡
        /// </summary>
        /// <param name="dir_path"></param>
        /// <param name="specific_bms_file_name">可选,钦定文件夹下某个bms谱面文件，如果不钦定就随机选取一个</param>
        /// <param name="start_time">起始时间，单位毫秒或者百分比，默认最初</param>
        /// <param name="end_time">终止时间，单位毫秒或者百分比，默认谱面末尾</param>
        /// <param name="encoder_command_line">编码命令</param>
        /// <param name="save_file_name">保存的文件名</param>
        public static bool GeneratePreviewAudio(
            string dir_path,
            string specific_bms_file_name = null,
            string start_time = null,
            string end_time = null,
            string encoder_command_line = "",
            string save_file_name = "preview_auto_generator.ogg",
            bool fast_clip = false)
        {
            var created_audio_handles = new HashSet<int>();
            var sync_record = new HashSet<int>(); 
            int mixer = 0;

            try
            {

                save_file_name = string.IsNullOrWhiteSpace(save_file_name) ? "preview_auto_generator.ogg" : save_file_name;

                if (!Directory.Exists(dir_path))
                    throw new Exception($"Directory {dir_path} not found.");

                var bms_file_path = string.IsNullOrWhiteSpace(specific_bms_file_name) ? Directory.EnumerateFiles(dir_path, "*.bms", SearchOption.TopDirectoryOnly).FirstOrDefault() : Path.Combine(dir_path, specific_bms_file_name);

                if (!File.Exists(bms_file_path))
                    throw new Exception($"BMS file {bms_file_path} not found.");

                Console.WriteLine($"BMS file path:{bms_file_path}");

                var content = File.ReadAllText(bms_file_path);

                if (CheckSkipable(dir_path, content))
                {
                    Console.WriteLine("This bms contains preview audio file, skiped.");
                    return true;
                }

                var chart = new BMS.BMSChart(content);
                chart.Parse(BMS.ParseType.Header);
                chart.Parse(BMS.ParseType.Resources);
                chart.Parse(BMS.ParseType.Content);


                var audio_map = chart.IterateResourceData(BMS.ResourceType.wav)
                    .Select(x => (x.resourceId, Directory.EnumerateFiles(dir_path, $"{Path.GetFileNameWithoutExtension(x.dataPath)}.*").FirstOrDefault()))
                    .Select(x => (x.resourceId, LoadAudio(x.Item2)))
                    .ToDictionary(x => x.resourceId, x => x.Item2);

                var bms_evemts = chart.Events
                    .Where(e => e.type ==
                    BMS.BMSEventType.WAV
                    || e.type == BMS.BMSEventType.Note
                    || e.type == BMS.BMSEventType.LongNoteEnd
                    || e.type == BMS.BMSEventType.LongNoteStart)
                    .OrderBy(x => x.time)
                    .Where(x => audio_map.ContainsKey(x.data2))
                    .ToArray();

                //init mixer
                mixer = BassMix.CreateMixerStream(44100, 2, BassFlags.Decode | BassFlags.MixerNonStop);

                //build triggers
                var mixer_events = new List<MixEventBase>(bms_evemts.Select(x => new AudioMixEvent()
                {
                    Time = x.time,
                    Duration = TimeSpan.FromSeconds(Bass.ChannelBytes2Seconds(audio_map[x.data2], Bass.ChannelGetLength(audio_map[x.data2]))),
                    PlayOffset = TimeSpan.Zero,
                    AudioHandle = audio_map[x.data2]
                }));

                #region Calculate and Adjust StartTime/EndTime

                var full_audio_duration = mixer_events.OfType<AudioMixEvent>().Max(x => x.Duration + x.Time).Add(TimeSpan.FromSeconds(1));
                var actual_end_time = string.IsNullOrWhiteSpace(end_time) ? full_audio_duration : (end_time.EndsWith("%") ? TimeSpan.FromMilliseconds(float.Parse(end_time.TrimEnd('%')) / 100.0f * full_audio_duration.TotalMilliseconds) : TimeSpan.FromMilliseconds(int.Parse(end_time)));
                var actual_start_time = string.IsNullOrWhiteSpace(start_time) ? TimeSpan.Zero : (start_time.EndsWith("%") ? TimeSpan.FromMilliseconds(float.Parse(start_time.TrimEnd('%')) / 100.0f * full_audio_duration.TotalMilliseconds) : TimeSpan.FromMilliseconds(int.Parse(start_time)));

                actual_start_time = actual_start_time < TimeSpan.Zero ? TimeSpan.Zero : actual_start_time;
                actual_start_time = actual_start_time > full_audio_duration ? full_audio_duration : actual_start_time;

                actual_end_time = actual_end_time < TimeSpan.Zero ? TimeSpan.Zero : actual_end_time;
                actual_end_time = actual_end_time > full_audio_duration ? full_audio_duration : actual_end_time;

                if (actual_end_time < actual_start_time)
                {
                    var t = actual_end_time;
                    actual_end_time = actual_start_time;
                    actual_start_time = t;
                }

                Console.WriteLine($"Actual Clip({(int)full_audio_duration.TotalMilliseconds}ms):{(int)actual_start_time.TotalMilliseconds}ms ~ {(int)actual_end_time.TotalMilliseconds}ms");

                #endregion

                if (fast_clip)
                    FastClipEvent(mixer_events, ref actual_start_time, ref actual_end_time);

                //add special events to control encorder and mixer
                mixer_events.Add(new StopMixEvent { Time = actual_end_time });
                mixer_events.Add(new StartMixEvent() { Time = actual_start_time });

                int encoder = 0;

                foreach (var evt in mixer_events)
                {
                    var trigger_position = Bass.ChannelSeconds2Bytes(mixer, evt.Time.TotalSeconds);

                    sync_record.Add(Bass.ChannelSetSync(mixer, SyncFlags.Position | SyncFlags.Mixtime, trigger_position, (nn, mm, ss, ll) =>
                    {
                        if (evt is StopMixEvent && encoder != 0)
                        {
                            Bass.ChannelStop(mixer);
                            BassEnc.EncodeStop(encoder);
                            encoder = 0;
                        }
                        else if (evt is StartMixEvent && encoder == 0)
                        {
                            var output_path = Path.Combine(dir_path, save_file_name);
                            Console.WriteLine($"Encoding output file path:{output_path}");
                            encoder = BassEnc_Ogg.Start(mixer, encoder_command_line, EncodeFlags.AutoFree, output_path);
                        }
                        else if (evt is AudioMixEvent audio)
                        {
                            var handle = audio.AudioHandle;
                            BassMix.MixerRemoveChannel(handle);
                            Bass.ChannelSetPosition(handle, Bass.ChannelSeconds2Bytes(handle, audio.PlayOffset.TotalSeconds));
                            BassMix.MixerAddChannel(mixer, handle, BassFlags.Default);
                        }
                    }));
                }

                WaitChannelDataProcessed(mixer);
                Console.WriteLine("Success!");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed.\n{ex.Message}\n{ex.StackTrace}");
                return false;
            }
            finally
            {
                #region Clean Resource

                foreach (var record in sync_record)
                    Bass.ChannelRemoveSync(mixer, record);

                foreach (var handle in created_audio_handles)
                    Bass.StreamFree(handle);

                Bass.StreamFree(mixer);

                #endregion
            }

            int LoadAudio(string item2)
            {
                var handle = Bass.CreateStream(item2, 0, 0, BassFlags.Decode | BassFlags.Float);

                created_audio_handles.Add(handle);

                return handle;
            }
        }

        private static void FastClipEvent(List<MixEventBase> mixer_events, ref TimeSpan actual_start_time, ref TimeSpan actual_end_time)
        {
            //remove events which out of range and never play
            var tst = actual_start_time;
            var tet = actual_end_time;
            var count = mixer_events.Count;
            mixer_events.RemoveAll(e => e is AudioMixEvent evt && (((evt.Time + evt.Duration) <= tst) || (evt.Time >= tet)));

            foreach (var evt in mixer_events.OfType<AudioMixEvent>().Where(x => x.Time < tst))
                evt.PlayOffset = tst - evt.Time;

            foreach (var evt in mixer_events)
                evt.Time -= tst;

            actual_start_time -= actual_start_time;
            actual_end_time -= actual_start_time;

            Console.WriteLine($"Fast clip:remove {mixer_events.Count - count} events,now is {(int)actual_start_time.TotalMilliseconds}ms ~ {(int)actual_end_time.TotalMilliseconds}ms");
        }

        private readonly static string[] support_extension_names = new[]
        {
            ".ogg",".mp3",".wav"
        };

        private static bool CheckSkipable(string dir_path, string content)
        {
            //check if there exist file named "preview*.(ogg|mp3|wav)"
            if (Directory.EnumerateFiles(dir_path, "preview*").Any(x => support_extension_names.Any(y => x.EndsWith(y,StringComparison.InvariantCultureIgnoreCase))))
                return true;

            if (content.Contains("#preview", StringComparison.InvariantCultureIgnoreCase))
                return true;

            return false;
        }

        private static void WaitChannelDataProcessed(int handle)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1024_000 * 10);

            while (true)
            {
                if (Bass.ChannelGetData(handle, buffer, buffer.Length) <= 0)
                    break;
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

﻿using ImageMagick;
using StableDiffusionGui.Data;
using StableDiffusionGui.Extensions;
using StableDiffusionGui.Forms;
using StableDiffusionGui.Io;
using StableDiffusionGui.MiscUtils;
using StableDiffusionGui.Os;
using StableDiffusionGui.Ui;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static StableDiffusionGui.Main.Enums.StableDiffusion;
using Path = System.IO.Path;
using Paths = StableDiffusionGui.Io.Paths;

namespace StableDiffusionGui.Main
{
    internal class TtiUtils
    {
        public static async Task<Dictionary<string, string>> CreateResizedInitImagesIfNeeded(List<string> initImgPaths, Size targetSize, bool print = false)
        {
            Logger.Log($"Importing initialization images...");

            Dictionary<string, string> sourceAndImportedPaths = initImgPaths.ToDictionary(x => x, x => ""); // Dictionary key = original path, Value is imported path
            int imgsSucessful = 0;
            int imgsResized = 0;

            var opts = new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount };
            Task parallelTask = Task.Run(async () => Parallel.For(0, sourceAndImportedPaths.Count, opts, async index =>
            {
                var pair = sourceAndImportedPaths.ElementAt(index);
                MagickImage img = new MagickImage(pair.Key) { Format = MagickFormat.Png24, Quality = 30 };

                if (img.Width == targetSize.Width && img.Height == targetSize.Height) // Size already matches
                {
                    Logger.Log($"Init img '{Path.GetFileName(pair.Key)}' has correct dimensions ({img.Width}x{img.Height}).", true);
                    sourceAndImportedPaths[pair.Key] = pair.Key; // Don't do anything, just assign the same input path as import path
                    Interlocked.Increment(ref imgsSucessful);
                }
                else // Needs to be resized
                {
                    try
                    {
                        Logger.Log($"Init img '{Path.GetFileName(pair.Key)}' has incorrect dimensions ({img.Width}x{img.Height}), resizing to {targetSize.Width}x{targetSize.Height}.", true);
                        img.Scale(new MagickGeometry(targetSize.Width, targetSize.Height) { IgnoreAspectRatio = true });
                        string initImgsDir = Directory.CreateDirectory(Path.Combine(Paths.GetSessionDataPath(), "inits")).FullName;
                        string resizedImgPath = Path.Combine(initImgsDir, $"{index}.png");
                        img.Write(resizedImgPath);
                        img.Dispose();
                        sourceAndImportedPaths[pair.Key] = resizedImgPath;
                        Interlocked.Increment(ref imgsSucessful);
                        Interlocked.Increment(ref imgsResized);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Failed to resize image: {ex.Message}\n{ex.StackTrace}", true);
                    }
                }
            }));

            while (!parallelTask.IsCompleted)
                await Task.Delay(1);

            Logger.Log($"Imported {imgsSucessful} images{(imgsResized > 0 ? $" - {imgsResized} were resized to {targetSize.Width}x{targetSize.Height}" : "")}.", false, Logger.LastUiLine.EndsWith("..."));
            return sourceAndImportedPaths;
        }

        /// <returns> Amount of removed images </returns>
        public static int CleanInitImageList()
        {
            if (MainUi.CurrentInitImgPaths == null)
                return 0;

            var modifiedList = MainUi.CurrentInitImgPaths.Where(path => File.Exists(path)).ToList();
            int removed = modifiedList.Count - MainUi.CurrentInitImgPaths.Count;

            if (MainUi.CurrentInitImgPaths.Count < 1)
            {
                MainUi.CurrentInitImgPaths = null;
                Logger.Log($"{(removed == 1 ? "Initialization image was cleared because the file no longer exists." : "Initialization images were cleared because the files no longer exist.")}");
            }
            else if (removed > 0)
            {
                MainUi.CurrentInitImgPaths = modifiedList;
                Logger.Log($"{removed} initialization image were removed because the files no longer exist.");
            }

            return removed;
        }

        /// <returns> Path to resized image </returns>
        public static string ResizeInitImg(string path, Size targetSize, bool print = false)
        {
            string outPath = Path.Combine(Paths.GetSessionDataPath(), "init.bmp");
            Image resized = ImgUtils.ResizeImage(IoUtils.GetImage(path), targetSize.Width, targetSize.Height);
            resized.Save(outPath, System.Drawing.Imaging.ImageFormat.Bmp);

            if (print)
                Logger.Log($"Resized init image to {targetSize.Width}x{targetSize.Height}.");

            return outPath;
        }

        public static void ShowPromptWarnings(List<string> prompts)
        {
            string longest = prompts.OrderByDescending(s => s.Length).First();
            longest = Regex.Replace(longest, @"(\[(?:\[??[^\[]*?\]))", "").Remove("[").Remove("]"); // Remove square brackets and contents

            char[] delimiters = new char[] { ' ', '\r', '\n' };
            int words = longest.Split(delimiters, StringSplitOptions.RemoveEmptyEntries).Length;

            int thresh = 55;

            if (words > thresh)
                UiUtils.ShowMessageBox($"{(prompts.Count > 1 ? "One of your prompts" : "Your prompt")} is very long (>{thresh} words).\n\nThe AI might ignore parts of your prompt. Shorten the prompt to avoid this.");

            if (Config.GetBool("checkboxOptimizedSd") && prompts.Where(x => x.MatchesRegex(@"(?:(?!\[)(?:.|\n))*\[(?:(?!\])(?:.|\n))*\]")).Any())
                UiUtils.ShowMessageBox($"{(prompts.Count > 1 ? "One of your prompts" : "Your prompt")} contains square brackets used for exclusion words.\n\nThis is currently not supported in Low Memory Mode.");

            if (MainUi.CurrentEmbeddingPath != null && MainUi.CurrentEmbeddingPath.ToLowerInvariant().EndsWith(".pt") && prompts.Any(x => !x.Contains("*")))
                UiUtils.ShowMessageBox($"{(prompts.Count > 1 ? "One of your prompts" : "Your prompt")} does not contain a concept placeholder (*).\n\nIt will not use your loaded concept.");
        }

        public static void SoftCancelInvokeAi()
        {
            OsUtils.SendCtrlC(TtiProcess.CurrentProcess.Id);

            var childProcesses = OsUtils.GetChildProcesses(TtiProcess.CurrentProcess);
            
            foreach (System.Diagnostics.Process p in childProcesses)
                OsUtils.SendCtrlC(p.Id);
        }

        /// <summary> Checks if Stable Diffusion model exists </summary>
        /// <returns> Model FileInfo, if it exists - null if not </returns>
        public static Model CheckIfCurrentSdModelExists(List<Model> cachedModels = null)
        {
            string name = Config.Get(Config.Key.comboxSdModel);
            var imp = (Implementation)Config.GetInt("comboxImplementation");

            if (string.IsNullOrWhiteSpace(name))
            {
                TextToImage.Cancel($"No Stable Diffusion model file has been set.\nPlease set one in the settings.");
                new SettingsForm().ShowDialogForm(0.5f);
                return null;
            }
            else
            {
                var model = cachedModels == null ? Paths.GetModel(name, false, ModelType.Normal, imp) : Paths.GetModel(cachedModels, name, false, ModelType.Normal, imp);

                if (model == null)
                {
                    TextToImage.Cancel($"Stable Diffusion model file {name.Wrap()} not found.\nPossibly it was moved, renamed, or deleted.");
                    return null;
                }
                else
                {
                    return model;
                }
            }
        }

        public static Dictionary<string, string> GetEnvVarsSd(bool allCudaDevices = false, string baseDir = ".", bool useConda = false)
        {
            var envVars = new Dictionary<string, string>();

            if (useConda)
            {
                string p = OsUtils.GetPathVar(new string[] {
                    Path.Combine(baseDir, Constants.Dirs.Conda),
                    Path.Combine(baseDir, Constants.Dirs.Conda, "Scripts"),
                    Path.Combine(baseDir, Constants.Dirs.Conda, "condabin"),
                    Path.Combine(baseDir, Constants.Dirs.Conda, "Scripts"),
                    Path.Combine(baseDir, Constants.Dirs.Conda, "Library", "bin"),
                    Path.Combine(baseDir, Constants.Dirs.Conda, "Scripts"),
                });

                envVars["PATH"] = p;
            }
            else
            {
                string p = OsUtils.GetPathVar(new string[] {
                    Path.Combine(baseDir, Constants.Dirs.SdVenv, "Scripts"), 
                    Path.Combine(baseDir, Constants.Dirs.Python, "Scripts"), 
                    Path.Combine(baseDir, Constants.Dirs.Python), 
                    Path.Combine(baseDir, Constants.Dirs.Git, "cmd")
                });

                envVars["PATH"] = p;
            }

            int cudaDeviceOpt = Config.GetInt("comboxCudaDevice");

            if (!allCudaDevices && cudaDeviceOpt > 0)
            {
                if (cudaDeviceOpt == 1) // CPU
                    envVars["CUDA_VISIBLE_DEVICES"] = "-1";
                else
                    envVars["CUDA_VISIBLE_DEVICES"] = $"{cudaDeviceOpt - 2}"; // Set env var to selected GPU ID (-2 because the first two options are Automatic and CPU)
            }

            if (!Directory.Exists(Path.Combine(Environment.ExpandEnvironmentVariables("%USERPROFILE%"), ".cache", "huggingface", "transformers")))
                envVars["TRANSFORMERS_CACHE"] = Path.Combine(Paths.GetDataPath(), Constants.Dirs.Cache, "trfm");

            if (!Directory.Exists(Path.Combine(Environment.ExpandEnvironmentVariables("%USERPROFILE%"), ".cache", "torch")))
                envVars["TORCH_HOME"] = Path.Combine(Paths.GetDataPath(), Constants.Dirs.Cache, "torch");

            return envVars;
        }

        public static string GetEnvVarsSdCommand(bool allCudaDevices = false, string baseDir = ".", bool useConda = false)
        {
            Dictionary<string, string> envVars = GetEnvVarsSd(allCudaDevices, baseDir, useConda);
            List<string> cmds = envVars.Select(x => $"SET \"{x.Key}={x.Value}\"").ToList();
            return string.Join(" && ", cmds);
        }

        public static bool ModelFilesizeValid(string path, Enums.StableDiffusion.ModelType type = Enums.StableDiffusion.ModelType.Normal)
        {
            if (!File.Exists(path))
                return false;

            return ModelFilesizeValid(new FileInfo(path).Length);
        }

        public static bool ModelFilesizeValid(Model model, Enums.StableDiffusion.ModelType type = Enums.StableDiffusion.ModelType.Normal)
        {
            if (!File.Exists(model.FullName))
                return false;

            return ModelFilesizeValid(model.Size);
        }

        public static bool ModelFilesizeValid(long size, Enums.StableDiffusion.ModelType type = Enums.StableDiffusion.ModelType.Normal)
        {
            try
            {
                if (type == Enums.StableDiffusion.ModelType.Normal)
                    return size > 2010000000;
            }
            catch
            {
                return true;
            }

            return true;
        }

        public static void ExportPostprocessedImage(string sourceImgPath, string processedImgPath)
        {
            string ext = Path.GetExtension(sourceImgPath);
            string movePath = GetUniquePath(Path.ChangeExtension(sourceImgPath, null) + $".fix" + ext);

            try
            {
                File.Move(processedImgPath, movePath);

                var meta = IoUtils.GetImageMetadata(sourceImgPath);
                IoUtils.SetImageMetadata(movePath, meta.ParsedText);

                ImagePreview.AppendImage(movePath, ImagePreview.ImgShowMode.ShowLast, false);
                // OsUtils.ShowNotification("Stable Diffusion GUI", $"Saved post-processed image as '{Path.GetFileName(movePath)}'.", false, 2.5f); // WHY DOES THIS NOT WORK
                Logger.Log($"Saved post-processed image as '{Path.GetFileName(movePath)}'.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save post-processed image: {ex.Message}");
                Logger.Log($"From '{processedImgPath}' to '{movePath}' - Trace:\n{ex.StackTrace}", true);
            }

            Program.MainForm.SetWorking(Program.BusyState.Standby);
        }

        private static string GetUniquePath(string preferredPath, string separator = "", int maxTries = 1000)
        {
            if (!File.Exists(preferredPath))
                return preferredPath;

            string pathNoExt = Path.ChangeExtension(preferredPath, null);
            string ext = Path.GetExtension(preferredPath);

            int counter = 1;

            while (File.Exists($"{pathNoExt}{separator}{counter}{ext}"))
            {
                counter++;

                if (counter >= maxTries)
                    return "";
            }

            return $"{pathNoExt}{separator}{counter}{ext}";
        }
    }
}

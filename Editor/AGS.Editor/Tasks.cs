using AGS.Types;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using AGS.Editor.Preferences;

namespace AGS.Editor
{
    public class Tasks
    {
        public const string AUTO_GENERATED_HEADER_NAME = "_AutoGenerated.ash";
        private const int MAX_GAME_FOLDER_NAME_LENGTH = 40;

        public delegate void TestGameFinishedHandler(int exitCode);
        public event TestGameFinishedHandler TestGameFinished;
        public delegate void GetFilesForInclusionInTemplateHandler(List<string> fileNames);
        public event GetFilesForInclusionInTemplateHandler GetFilesForInclusionInTemplate;
        // Fired when a new game directory has just been created from a template,
        // but before the game is loaded into AGS.
        public delegate void NewGameFilesExtractedHandler();
        public event NewGameFilesExtractedHandler NewGameFilesExtracted;

        private Process _testGameProcess = null;
        private bool _runningGameWithDebugger = false;

        public void CreateNewGameFromTemplate(string templateFileName, string newGameDirectory)
        {
            Directory.CreateDirectory(newGameDirectory);
            Directory.SetCurrentDirectory(newGameDirectory);
            Utilities.EnsureStandardSubFoldersExist();
            Factory.NativeProxy.ExtractTemplateFiles(templateFileName);

            if (NewGameFilesExtracted != null)
            {
                NewGameFilesExtracted();
            }
        }

        private void ConstructBasicFileListForTemplate(List<string> filesToInclude, List<string> filesToDeleteAfterwards)
        {
            Utilities.AddAllMatchingFiles(filesToInclude, "*.ico");
            Utilities.AddAllMatchingFiles(filesToInclude, AGSEditor.GAME_FILE_NAME);
            Utilities.AddAllMatchingFiles(filesToInclude, AGSEditor.SPRITE_FILE_NAME);
            Utilities.AddAllMatchingFiles(filesToInclude, "preload.pcx");
            Utilities.AddAllMatchingFiles(filesToInclude, AudioClip.AUDIO_CACHE_DIRECTORY + @"\*.*");
            Utilities.AddAllMatchingFiles(filesToInclude, @"Speech\*.*");
            Utilities.AddAllMatchingFiles(filesToInclude, "flic*.fl?");
            Utilities.AddAllMatchingFiles(filesToInclude, "agsfnt*.ttf");
            Utilities.AddAllMatchingFiles(filesToInclude, "agsfnt*.wfn");
            Utilities.AddAllMatchingFiles(filesToInclude, "*.crm");
            Utilities.AddAllMatchingFiles(filesToInclude, "*.asc");
            Utilities.AddAllMatchingFiles(filesToInclude, "*.ash");
            Utilities.AddAllMatchingFiles(filesToInclude, "*.txt");
            Utilities.AddAllMatchingFiles(filesToInclude, "*.trs");
            Utilities.AddAllMatchingFiles(filesToInclude, "*.pdf");
            Utilities.AddAllMatchingFiles(filesToInclude, "*.ogv");

            if (GetFilesForInclusionInTemplate != null)
            {
                List<string> extraFiles = new List<string>();

                GetFilesForInclusionInTemplate(extraFiles);

                foreach (string fullFileName in extraFiles)
                {
                    string baseFileName = Path.GetFileName(fullFileName);
                    if (Path.GetDirectoryName(fullFileName).ToLower() != Directory.GetCurrentDirectory().ToLower())
                    {
                        File.Copy(fullFileName, baseFileName, true);
                        filesToDeleteAfterwards.Add(baseFileName);
                    }
                    filesToInclude.Add(baseFileName);
                }
            }

        }

        public void CreateTemplateFromCurrentGame(string templateFileName)
        {
            List<string> files = new List<string>();
            List<string> filesToDeleteAfterwards = new List<string>();

            ConstructBasicFileListForTemplate(files, filesToDeleteAfterwards);

            if (File.Exists(templateFileName))
            {
                File.Delete(templateFileName);
            }

            Factory.NativeProxy.CreateTemplateFile(templateFileName, files.ToArray());

            foreach (string fileName in filesToDeleteAfterwards)
            {
                File.Delete(fileName);
            }
        }

        public bool LoadGameFromDisk(string gameToLoad, bool interactive)
        {
            bool needToSave = false;
            string gameDirectory = Path.GetDirectoryName(gameToLoad);

            if (Path.GetFileName(gameDirectory).Length > MAX_GAME_FOLDER_NAME_LENGTH)
            {
                throw new AGSEditorException("This game cannot be loaded because it is in a folder that has a name longer than 40 characters.");
            }

            List<string> errors = new List<string>();

            Directory.SetCurrentDirectory(gameDirectory);
            AddFontIfNotAlreadyThere(0);
            AddFontIfNotAlreadyThere(1);
            AddFontIfNotAlreadyThere(2);
            Game game = null;

            if (gameToLoad.ToLower().EndsWith(".dta"))
            {
                game = new OldGameImporter().ImportGameFromAGS272(gameToLoad, interactive);
                needToSave = true;
            }
            else
            {
                Factory.AGSEditor.LoadGameFile(gameToLoad);
                Factory.NativeProxy.LoadNewSpriteFile();
                game = Factory.AGSEditor.CurrentGame;
            }

            if (game != null)
            {
                game.DirectoryPath = gameDirectory;
                SetDefaultValuesForNewFeatures(game);

                Utilities.EnsureStandardSubFoldersExist();

                RecentGame recentGame = new RecentGame(game.Settings.GameName, gameDirectory);
                if (Factory.AGSEditor.Settings.RecentGames.Contains(recentGame))
                {
                    Factory.AGSEditor.Settings.RecentGames.Remove(recentGame);
                }
                Factory.AGSEditor.Settings.RecentGames.Insert(0, recentGame);

                Factory.Events.OnGamePostLoad();

                Factory.AGSEditor.RefreshEditorAfterGameLoad(game, errors);
                if (needToSave)
                {
                    Factory.AGSEditor.SaveGameFiles();
                }

                Factory.AGSEditor.ReportGameLoad(errors);
                return true;
            }

            return false;
        }

        private void SetDefaultValuesForNewFeatures(Game game)
        {
            // TODO: this may be noticably if upgrading lots of items. Display some kind of
            // progress window to notify user.

            int xmlVersionIndex = 0;
            if (game.SavedXmlVersionIndex.HasValue)
            {
                xmlVersionIndex = game.SavedXmlVersionIndex.Value;
            }

            if (xmlVersionIndex < 2)
            {
                // Upgrade old games to use the Anti-Glide Mode setting
                foreach (Character character in game.RootCharacterFolder.AllItemsFlat)
                {
                    character.MovementLinkedToAnimation = game.Settings.AntiGlideMode;
                }
            }

            if (xmlVersionIndex < 3)
            {
                // Upgrade old games to flatten the dialog scripts
                foreach (Dialog dialog in game.RootDialogFolder.AllItemsFlat)
                {
                    dialog.Script = RemoveAllLeadingSpacesFromLines(dialog.Script);
                }
            }

            if (xmlVersionIndex < 15)
            {
                game.DefaultSetup.SetDefaults();
            }

            if (xmlVersionIndex < 18)
            {
                // Promote sprites to "real" resolution when possible (ideally almost always)
                foreach (Sprite sprite in game.RootSpriteFolder.GetAllSpritesFromAllSubFolders())
                {
                    sprite.Resolution = Utilities.FixupSpriteResolution(sprite.Resolution);
                }
            }

            if (xmlVersionIndex < 18)
            {
                foreach (Font font in game.Fonts)
                    font.SizeMultiplier = 1;
                // Apply font scaling to each individual font settings.
                // Bitmap fonts save multiplier explicitly, while vector fonts have their size doubled.
                if (game.IsHighResolution && !game.Settings.FontsForHiRes)
                {
                    foreach (Font font in game.Fonts)
                    {
                        if (font.PointSize == 0)
                        {
                            font.SizeMultiplier = 2;
                        }
                        else
                        {
                            font.PointSize *= 2;
                        }
                    }
                }
            }

            if (xmlVersionIndex < 18)
            {
                game.Settings.AllowRelativeAssetResolutions = true;
                game.Settings.DefaultRoomMaskResolution = game.IsHighResolution ? 2 : 1;
            }

            if (xmlVersionIndex < 19)
            {
                game.Settings.GameFileName = AGSEditor.Instance.BaseGameFileName;

                var buildNames = new Dictionary<string, string>();
                foreach (IBuildTarget target in BuildTargetsInfo.GetRegisteredBuildTargets())
                {
                    buildNames[target.Name] = AGSEditor.Instance.BaseGameFileName;
                }
                game.WorkspaceState.SetLastBuildGameFiles(buildNames);
            }

            if (xmlVersionIndex < 20)
            {
                // Set the alpha channel requests for re-import based on the presence of an alpha channel
                foreach (Sprite sprite in game.RootSpriteFolder.GetAllSpritesFromAllSubFolders())
                {
                    sprite.ImportAlphaChannel = sprite.AlphaChannel;
                }
            }

            if (xmlVersionIndex < 21)
            {
                // Assign audio clip ids to match and solidify their current position in AudioClips array.
                int clipId = 0;
                Dictionary<int, int> audioIndexToID = new Dictionary<int, int>();
                foreach (AudioClip clip in game.RootAudioClipFolder.GetAllAudioClipsFromAllSubFolders())
                {
                    clip.ID = clipId++;
                    audioIndexToID.Add(clip.Index, clip.ID);
                }
                game.RootAudioClipFolder.Sort(true);

                // Remap old cache indexes to new IDs
                if (game.Settings.PlaySoundOnScore == 0)
                {
                    game.Settings.PlaySoundOnScore = -1;
                }
                else
                {
                    int id;
                    if (audioIndexToID.TryGetValue(game.Settings.PlaySoundOnScore, out id))
                        game.Settings.PlaySoundOnScore = id;
                    else
                        game.Settings.PlaySoundOnScore = -1;
                }

                foreach (Types.View view in game.RootViewFolder.AllItemsFlat)
                {
                    foreach (Types.ViewLoop loop in view.Loops)
                    {
                        foreach (Types.ViewFrame frame in loop.Frames)
                        {
                            if (frame.Sound == 0)
                            {
                                frame.Sound = -1;
                            }
                            else
                            {
                                int id;
                                if (audioIndexToID.TryGetValue(frame.Sound, out id))
                                    frame.Sound = id;
                                else
                                    frame.Sound = -1;
                            }
                        }
                    }
                }
            }

            if (xmlVersionIndex < 22)
            {
                game.Settings.ScaleMovementSpeedWithMaskResolution = true;
            }

            if (xmlVersionIndex < 23)
            {
                // Set the import dimensions based on existing sprite dimensions
                foreach (Sprite sprite in game.RootSpriteFolder.GetAllSpritesFromAllSubFolders())
                {
                    sprite.ImportWidth = sprite.Width;
                    sprite.ImportHeight = sprite.Height;
                }
            }

            if (xmlVersionIndex < 24)
            {
                // get all known source images and their largest known size
                // (avoiding System.Drawing / GDI as a dependency to load the project)
                Dictionary<string, Tuple<int, int>> sourceMaxSize = new Dictionary<string, Tuple<int, int>>(StringComparer.OrdinalIgnoreCase);

                foreach (Sprite sprite in game.RootSpriteFolder.GetAllSpritesFromAllSubFolders())
                {
                    if (!string.IsNullOrWhiteSpace(sprite.SourceFile))
                    {
                        int currentX = sprite.OffsetX + sprite.ImportWidth;
                        int currentY = sprite.OffsetY + sprite.ImportHeight;

                        if (sourceMaxSize.ContainsKey(sprite.SourceFile))
                        {
                            int maxX = sourceMaxSize[sprite.SourceFile].Item1;
                            int maxY = sourceMaxSize[sprite.SourceFile].Item2;
                            if (maxX < currentX) maxX = currentX;
                            if (maxY < currentY) maxY = currentY;
                            sourceMaxSize[sprite.SourceFile] = Tuple.Create(maxX, maxY);
                        }
                        else
                        {
                            sourceMaxSize.Add(sprite.SourceFile, Tuple.Create(currentX, currentY));
                        }
                    }
                }

                // Set the tiled image flag for existing imports - the only misdetection would be
                // a single import from a source image that starts at 0,0, but wasn't for the
                // entire image
                foreach (Sprite sprite in game.RootSpriteFolder.GetAllSpritesFromAllSubFolders())
                {
                    if (sprite.OffsetX > 0 || sprite.OffsetY > 0)
                    {
                        sprite.ImportAsTile = true;
                    }
                    else if (sourceMaxSize.ContainsKey(sprite.SourceFile))
                    {
                        int maxX = sourceMaxSize[sprite.SourceFile].Item1;
                        int maxY = sourceMaxSize[sprite.SourceFile].Item2;
                        sprite.ImportAsTile = sprite.ImportWidth < maxX || sprite.ImportHeight < maxY;
                    }
                    else
                    {
                        sprite.ImportAsTile = false;
                    }
                }
            }

            System.Version editorVersion = new System.Version(AGS.Types.Version.AGS_EDITOR_VERSION);
            System.Version projectVersion = game.SavedXmlEditorVersion != null ? Types.Utilities.TryParseVersion(game.SavedXmlEditorVersion) : null;
            if (projectVersion == null || projectVersion < editorVersion)
                game.SetScriptAPIForOldProject();
        }

        private string RemoveAllLeadingSpacesFromLines(string script)
        {
            StringReader sr = new StringReader(script);
            StringWriter sw = new StringWriter();
            string thisLine;
            while ((thisLine = sr.ReadLine()) != null)
            {
                sw.WriteLine(thisLine.Trim());
            }
            string returnValue = sw.ToString();
            sr.Close();
            sw.Close();
            return returnValue;
        }

        private void AddFontIfNotAlreadyThere(int fontNumber)
        {
            if ((!File.Exists("agsfnt" + fontNumber + ".wfn")) &&
                (!File.Exists("agsfnt" + fontNumber + ".ttf")))
            {
                Resources.ResourceManager.CopyFileFromResourcesToDisk("AGSFNT" + fontNumber + ".WFN");
            }
        }

        public void RunGameSetup()
        {
            RunGameEXE("--setup", false);
        }

        public void TestGame(bool withDebugger)
        {
            string parameter = string.Empty;
            if (withDebugger)
            {
                // debugger connection params
                parameter = "--enabledebugger " + Factory.AGSEditor.Debugger.InstanceIdentifier;
            }
            else if (Factory.AGSEditor.Settings.TestGameWindowStyle == TestGameWindowStyle.Windowed)
            {
                parameter = "-windowed";
            }
            else if (Factory.AGSEditor.Settings.TestGameWindowStyle == TestGameWindowStyle.FullScreen)
            {
                parameter = "-fullscreen";
            }
            _runningGameWithDebugger = withDebugger;
            // custom game install directory (points to where all supplemental data files are)
            // TODO: get audio and speech paths from a kind of shared config
            parameter += " --runfromide " + Path.Combine(AGSEditor.OUTPUT_DIRECTORY, BuildTargetWindows.WINDOWS_DIRECTORY) +
                         " " + AudioClip.AUDIO_CACHE_DIRECTORY + " " + "Speech";

            RunEXEFile(Path.Combine(AGSEditor.DEBUG_OUTPUT_DIRECTORY, Factory.AGSEditor.BaseGameFileName + ".exe"), parameter, true);

            if (withDebugger)
            {
                Factory.AGSEditor.Debugger.InitializeEngine(Factory.AGSEditor.CurrentGame, Factory.GUIController.TopLevelWindowHandle);
            }

        }

        private void RunGameEXE(string parameter, bool raiseEventOnExit)
        {
            string gameDirectory = Directory.GetCurrentDirectory();
            try
            {
                string exeName = Factory.AGSEditor.BaseGameFileName + ".exe";
                string exeDir = Path.Combine(AGSEditor.OUTPUT_DIRECTORY, BuildTargetWindows.WINDOWS_DIRECTORY);
                Directory.CreateDirectory(exeDir); // creates Windows directory if it does not exist
                Directory.SetCurrentDirectory(exeDir); // change into Windows directory to run setup

                RunEXEFile(exeName, parameter, raiseEventOnExit);
            }
            finally
            {
                Directory.SetCurrentDirectory(gameDirectory);
            }
        }

        private void RunEXEFile(string exeName, string parameter, bool raiseEventOnExit)
        {
            try
            {
                if (!File.Exists(exeName))
                {
                    throw new FileNotFoundException("Game EXE '" + exeName + "' has not been built. Use the Build EXE command and then try again.");
                }

                _testGameProcess = new Process();
                _testGameProcess.StartInfo.FileName = exeName;
                _testGameProcess.StartInfo.Arguments = parameter;
                if (raiseEventOnExit)
                {
                    _testGameProcess.EnableRaisingEvents = true;
                    _testGameProcess.Exited += new EventHandler(_testGameProcess_Exited);
                }
                _testGameProcess.Start();
            }
            catch (Exception ex)
            {
                if (raiseEventOnExit)
                {
                    _testGameProcess_Exited(null, null);
                }
                throw ex;
            }
        }

        private void _testGameProcess_Exited(object sender, EventArgs e)
        {
            if (_runningGameWithDebugger)
            {
                Factory.AGSEditor.Debugger.EngineHasExited();
                _runningGameWithDebugger = false;
            }

            if (TestGameFinished != null)
            {
                int exitCode = -1;
                try
                {
                    // the ExitCode property will throw an exception
                    // if the process didn't start, in which case
                    // we use -1 as the exit code
                    exitCode = _testGameProcess.ExitCode;
                }
                catch (InvalidOperationException) { }

                TestGameFinished(exitCode);
            }

            _testGameProcess = null;
        }

        public Script RegenerateScriptHeader(Game game, Room currentRoom)
        {
            StringBuilder sb = new StringBuilder(10000);
            //            sb.AppendLine("#define AGS_MAX_CHARACTERS " + Game.MAX_CHARACTERS);
            sb.AppendLine("#define AGS_MAX_INV_ITEMS " + Game.MAX_INV_ITEMS);
            //            sb.AppendLine("#define AGS_MAX_GUIS " + Game.MAX_GUIS);
            sb.AppendLine("#define AGS_MAX_CONTROLS_PER_GUI " + GUI.LEGACY_MAX_CONTROLS_PER_GUI);
            //            sb.AppendLine("#define AGS_MAX_VIEWS " + Game.MAX_VIEWS);
            //            sb.AppendLine("#define AGS_MAX_LOOPS_PER_VIEW " + AGS.Types.View.MAX_LOOPS_PER_VIEW);
            //            sb.AppendLine("#define AGS_MAX_FRAMES_PER_LOOP " + ViewLoop.MAX_FRAMES_PER_LOOP);
            sb.AppendLine("#define AGS_MAX_OBJECTS " + Room.MAX_OBJECTS);
            sb.AppendLine("#define AGS_MAX_HOTSPOTS " + Room.MAX_HOTSPOTS);
            sb.AppendLine("#define AGS_MAX_REGIONS " + Room.MAX_REGIONS);

            AppendCursorsToHeader(sb, game.Cursors);

            AppendFontsToHeader(sb, game.Fonts);

            AppendCharactersToHeader(sb, game.RootCharacterFolder, game);

            AppendAudioClipTypesToHeader(sb, game.AudioClipTypes);

            AppendAudioClipsToHeader(sb, game.RootAudioClipFolder);

            sb.AppendLine("import Hotspot hotspot[" + Room.MAX_HOTSPOTS + "];");
            sb.AppendLine("import Region region[" + Room.MAX_REGIONS + "];");

            AppendInventoryToHeader(sb, game.InventoryItems);

            AppendDialogsToHeader(sb, game.Dialogs);

            AppendGUIsToHeader(sb, game.GUIs);

            AppendViewsToHeader(sb, game.RootViewFolder);

            if (currentRoom != null)
            {
                AppendRoomObjectsAndHotspotsToHeader(sb, currentRoom);
            }

            return new Script(AUTO_GENERATED_HEADER_NAME, sb.ToString(), true);
        }

        private void AppendRoomObjectsAndHotspotsToHeader(StringBuilder sb, Room room)
        {
            foreach (RoomObject obj in room.Objects)
            {
                if (obj.Name.Length > 0)
                {
                    sb.AppendLine("import Object " + obj.Name + ";");
                }
            }

            foreach (RoomHotspot hotspot in room.Hotspots)
            {
                if (hotspot.Name.Length > 0)
                {
                    sb.AppendLine("import Hotspot " + hotspot.Name + ";");
                }
            }
        }

        private void AppendInventoryToHeader(StringBuilder sb, IList<InventoryItem> items)
        {
            if (items.Count > 0)
            {
                sb.AppendLine("import InventoryItem inventory[" + (items.Count + 1) + "];");
                foreach (InventoryItem item in items)
                {
                    if (item.Name.Length > 0)
                    {
                        sb.AppendLine("import InventoryItem " + item.Name + ";");
                    }
                }
            }
        }

        private void AppendDialogsToHeader(StringBuilder sb, IList<Dialog> dialogs)
        {
            if (dialogs.Count > 0)
            {
                sb.AppendLine("import Dialog dialog[" + dialogs.Count + "];");
                foreach (Dialog item in dialogs)
                {
                    if (item.Name.Length > 0)
                    {
                        sb.AppendLine("import Dialog " + item.Name + ";");
                    }
                }
            }
        }

        private void AppendViewsToHeader(StringBuilder sb, ViewFolder viewFolder)
        {
            foreach (AGS.Types.View view in viewFolder.Views)
            {
                if (view.Name.Length > 0)
                {
                    sb.AppendLine("#define " + view.Name.ToUpper() + " " + view.ID);
                }
            }

            foreach (ViewFolder subFolder in viewFolder.SubFolders)
            {
                AppendViewsToHeader(sb, subFolder);
            }
        }

        private void AppendGUIsToHeader(StringBuilder sb, IList<GUI> guis)
        {
            if (guis.Count > 0)
            {
                sb.AppendLine("import GUI gui[" + guis.Count + "];");

                foreach (GUI gui in guis)
                {
                    if (gui.Name.Length == 0)
                    {
                        continue;
                    }

                    sb.AppendLine("import GUI " + gui.Name + ";");

                    if (gui.Name.StartsWith("g"))
                    {
                        string guiMacroName = gui.Name.Substring(1).ToUpper();
                        sb.AppendLine(string.Format("#define {0} FindGUIID(\"{1}\")", guiMacroName, guiMacroName));
                    }

                    foreach (GUIControl control in gui.Controls)
                    {
                        if (control.Name.Length > 0)
                        {
                            sb.AppendLine("import " + control.ScriptClassType + " " + control.Name + ";");
                        }
                    }
                }
            }
        }

        private void AppendAudioClipsToHeader(StringBuilder sb, AudioClipFolder clips)
        {
            foreach (AudioClip clip in clips.AllItemsFlat)
            {
                sb.AppendLine("import AudioClip " + clip.ScriptName + ";");
            }
        }

        private void AppendCharactersToHeader(StringBuilder sb, CharacterFolder characters, Game game)
        {
            int charactersCount = characters.GetAllItemsCount();
            if (charactersCount > 0)
            {
                sb.AppendLine(string.Format("import Character character[{0}];", charactersCount));

                foreach (Character character in characters.AllItemsFlat)
                {
                    if (character.ScriptName.StartsWith("c") &&
                        (character.ScriptName.Length > 1))
                    {
                        string macroName = character.ScriptName.Substring(1).ToUpper();
                        // only create the legacy #define if it doesn't start with 0-9
                        // (eg. c500 would cause error)
                        if (!Char.IsDigit(macroName[0]))
                        {
                            sb.AppendLine("#define " + macroName + " " + character.ID);
                        }
                    }
                    if (character.ScriptName.Length > 0)
                    {
                        sb.AppendLine("import Character " + character.ScriptName + ";");
                    }
                }
            }
        }

        private void AppendCursorsToHeader(StringBuilder sb, IList<MouseCursor> cursors)
        {
            sb.AppendLine("enum CursorMode {");
            bool firstCursor = true;
            foreach (MouseCursor cursor in cursors)
            {
                string cursorName = cursor.ScriptID;
                if (cursorName.Length > 0)
                {
                    if (!firstCursor)
                    {
                        sb.AppendLine(",");
                    }
                    sb.Append("  " + cursorName + " = " + cursor.ID);
                    firstCursor = false;
                }
            }
            if (firstCursor)
            {
                // no cursors, make sure the enum has something in it
                sb.Append("eDummyCursor__ = 99  // $AUTOCOMPLETEIGNORE$ ");
            }
            sb.AppendLine();
            sb.AppendLine("};");
        }

        private void AppendFontsToHeader(StringBuilder sb, IList<AGS.Types.Font> fonts)
        {
            sb.AppendLine("enum FontType {");
            bool firstFont = true;
            foreach (AGS.Types.Font font in fonts)
            {
                string fontName = font.ScriptID;
                if (fontName.Length > 0)
                {
                    if (!firstFont)
                    {
                        sb.AppendLine(",");
                    }
                    sb.Append("  " + fontName + " = " + font.ID);
                    firstFont = false;
                }
            }
            if (firstFont)
            {
                // no cursors, make sure the enum has something in it
                sb.Append("eDummyFont__ = 99  // $AUTOCOMPLETEIGNORE$ ");
            }
            sb.AppendLine();
            sb.AppendLine("};");
        }

        private void AppendAudioClipTypesToHeader(StringBuilder sb, IList<AGS.Types.AudioClipType> clipTypes)
        {
            sb.AppendLine("enum AudioType {");
            bool firstType = true;
            foreach (AGS.Types.AudioClipType clipType in clipTypes)
            {
                string scriptName = clipType.ScriptID;
                if (scriptName.Length > 0)
                {
                    if (!firstType)
                    {
                        sb.AppendLine(",");
                    }
                    sb.Append("  " + scriptName + " = " + clipType.TypeID);
                    firstType = false;
                }
            }
            if (firstType)
            {
                // no clip types, make sure the enum has something in it
                sb.Append("eDummyAudioType__ = 99  // $AUTOCOMPLETEIGNORE$ ");
            }
            sb.AppendLine();
            sb.AppendLine("};");
        }

    }
}

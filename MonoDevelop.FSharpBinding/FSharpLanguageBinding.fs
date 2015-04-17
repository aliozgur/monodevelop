namespace MonoDevelop.FSharp

open System
open System.Xml
open System.CodeDom.Compiler
open System.IO

open MonoDevelop.Core
open MonoDevelop.Ide
open MonoDevelop.Ide.Gui.Content
open MonoDevelop.Projects
open FSharp.Compiler
open FSharp.CompilerBinding
open System.Linq
open MonoDevelop.Projects.Formats.MSBuild


type CorrectGuidMSBuildExtension() =
    inherit MSBuildExtension()

    override x.SaveProject (_monitor, _item, project) =
        try
            let fsimportExists =
                project.Imports
                |> Seq.exists (fun import -> import.Project.EndsWith ("FSharp.Targets", StringComparison.OrdinalIgnoreCase))

            if fsimportExists then
                project.GetGlobalPropertyGroup().Properties
                |> Seq.tryFind (fun p -> p.Name = "ProjectTypeGuids")
                |> Option.iter
                    (fun guids ->
                        guids.Element.InnerText <-
                            guids.Element.InnerText.Split ([|';'|], StringSplitOptions.RemoveEmptyEntries)
                            |> Array.filter (fun guid -> not (guid.Equals ("{4925A630-B079-445D-BCD4-3A9C94FE9307}", StringComparison.OrdinalIgnoreCase)))
                            |> String.concat ";" )

         with exn -> LoggingService.LogWarning ("Failed to remove old F# guid", exn)


type FSharpLanguageBinding() =
  static let LanguageName = "F#"

  let provider = lazy new CodeDom.FSharpCleanCodeProvider()
  let langServ = MDLanguageService.Instance
    
  let invalidateProjectFile(project:Project) =
    match project with
    | :? DotNetProject as dnp when dnp.LanguageName = LanguageName ->
        try
            let options = langServ.GetProjectCheckerOptions(dnp.FileName.ToString(), [("Configuration", IdeApp.Workspace.ActiveConfigurationId)])
            langServ.InvalidateConfiguration(options)
            langServ.ClearProjectInfoCache()
        with ex -> LoggingService.LogError ("Could not invalidate configuration", ex)
    | _ -> ()
    
  let invalidateFiles (args:#ProjectFileEventInfo seq) =
    for projectFileEvent in args do
        if MDLanguageService.SupportedFileName (projectFileEvent.ProjectFile.FilePath.ToString()) then
            invalidateProjectFile(projectFileEvent.Project)

  let invalidateConfig _args =
      IdeApp.Workspace.GetAllProjects()
      |> Seq.iter invalidateProjectFile

  let eventDisposer =
      ResizeArray<IDisposable> ()
            
  // Watch for changes that trigger a reparse, but only if we're running within the IDE context
  // and not from mdtool or something like it.
  do if IdeApp.IsInitialized then
      //Add events to invalidate FCS if anything imprtant to do with configuration changes
      //e.g. Files added/removed/renamed, or references added/removed      
      IdeApp.Workspace.ActiveConfigurationChanged.Subscribe(invalidateConfig) |> eventDisposer.Add
      IdeApp.Workspace.FileAddedToProject.Subscribe(invalidateFiles) |> eventDisposer.Add
      IdeApp.Workspace.FileRemovedFromProject.Subscribe(invalidateFiles) |> eventDisposer.Add
      IdeApp.Workspace.FileRenamedInProject.Subscribe(invalidateFiles) |> eventDisposer.Add
      IdeApp.Workspace.FilePropertyChangedInProject.Subscribe(invalidateFiles) |> eventDisposer.Add
      IdeApp.Workspace.ReferenceAddedToProject.Subscribe(fun (r:ProjectReferenceEventArgs) -> invalidateProjectFile(r.Project)) |> eventDisposer.Add
      IdeApp.Workspace.ReferenceRemovedFromProject.Subscribe(fun (r:ProjectReferenceEventArgs) -> invalidateProjectFile(r.Project)) |> eventDisposer.Add
      IdeApp.Workspace.SolutionUnloaded.Subscribe(fun _ -> langServ.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()) |> eventDisposer.Add



  // Keep the platforms combo of CodeGenerationPanelWidget in sync with this list
  let supportedPlatforms = [| "anycpu"; "x86"; "x64"; "itanium" |]
  interface IDotNetLanguageBinding  with
    member x.BlockCommentEndTag = "*)"
    member x.BlockCommentStartTag = "(*"
    member x.Language = LanguageName
    member x.SingleLineCommentTag = "//"
    member x.GetFileName(baseName) = new FilePath(baseName.ToString() + ".fs")
    member x.IsSourceCodeFile(fileName) = MDLanguageService.SupportedFileName (fileName.ToString())
    
    // IDotNetLanguageBinding
    override x.Compile(items, config, configSel, monitor) : BuildResult =
      CompilerService.Compile(items, config, configSel, monitor)

    override x.CreateCompilationParameters(options:XmlElement) : ConfigurationParameters =
      let pars = new FSharpCompilerParameters() 
      // Set up the default options
      if options <> null then 
          let platform = options.GetAttribute ("Platform")
          if (supportedPlatforms |> Array.exists (fun x -> x.Contains (platform))) then
              pars.PlatformTarget <- platform

          let debugAtt = options.GetAttribute ("DefineDebug")
          if (System.String.Compare ("True", debugAtt, StringComparison.OrdinalIgnoreCase) = 0) then
              pars.AddDefineSymbol "DEBUG"
              pars.DebugSymbols <- true
              pars.Optimize <- false
              pars.GenerateTailCalls <- false
          let releaseAtt = options.GetAttribute ("Release")
          if (System.String.Compare ("True", releaseAtt, StringComparison.OrdinalIgnoreCase) = 0) then
              pars.DebugSymbols <- false
              pars.Optimize <- true
              pars.GenerateTailCalls <- true
      // TODO: set up the documentation file to be AssemblyName.xml by default (but how do we get AssemblyName here?)
      // pars.DocumentationFile <- ""
      //    System.IO.Path.GetFileNameWithoutExtension(config.CompiledOutputName.ToString())+".xml" 
      pars :> ConfigurationParameters


    override x.CreateProjectParameters(_options:XmlElement) : ProjectParameters =
      ProjectParameters()
      
    override x.GetCodeDomProvider() : CodeDomProvider =
        provider.Value :> CodeDomProvider
      
    override x.GetSupportedClrVersions() =
      [| ClrVersion.Net_2_0; ClrVersion.Net_4_0; ClrVersion.Net_4_5;  ClrVersion.Clr_2_1 |]

    override x.ProjectStockIcon = "md-project"

  interface IDisposable with
    member x.Dispose () = 
      for disp in eventDisposer do
        disp.Dispose ()

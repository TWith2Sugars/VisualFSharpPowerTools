﻿namespace FSharpVSPowerTools.Navigation

open System
open System.Threading
open System.IO
open System.Windows
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio
open Microsoft.VisualStudio.OLE.Interop
open Microsoft.VisualStudio.Shell.Interop
open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.FSharp.Compiler
open FSharpVSPowerTools
open FSharpVSPowerTools.ProjectSystem
open FSharp.CompilerBinding

[<RequireQualifiedAccess>]
module PkgCmdConst =
    let cmdidFindReferences = uint32 VSConstants.VSStd97CmdID.FindReferences
    let guidBuiltinCmdSet = VSConstants.GUID_VSStandardCommandSet97
    let guidSymbolLibrary = Guid("2ad4e2a2-b89f-48b6-98e8-363bd1a35450")

[<NoEquality; NoComparison>]
type DocumentState =
    { Word: SnapshotSpan * Symbol
      File: string
      Project: IProjectProvider
      TargetProjects: IProjectProvider list }

type FindReferencesFilter(view: IWpfTextView, vsLanguageService: VSLanguageService, serviceProvider: System.IServiceProvider) =
    let getDocumentState() =
        async {
            let dte = serviceProvider.GetService<EnvDTE.DTE, SDTE>()
            let projectItems = maybe {
                let! caretPos = view.TextBuffer.GetSnapshotPoint view.Caret.Position
                let! doc = dte.GetActiveDocument()
                let! project = ProjectProvider.createForDocument doc
                let! span, sym = vsLanguageService.GetSymbol(caretPos, project)
                return doc.FullName, project, span, sym }

            match projectItems with
            | Some (file, project, span, sym) ->
                let! symbolUse = vsLanguageService.GetFSharpSymbolUse(span, sym, file, project, AllowStaleResults.No)
                match symbolUse with
                | Some (symbolUse, _) ->
                    let targetProjects =
                        match ProjectProvider.getSymbolUsageScope symbolUse.Symbol dte file with
                        | Some symbolDeclLocation ->
                            let declProject = 
                                match symbolDeclLocation with
                                | SymbolDeclarationLocation.File -> project
                                | SymbolDeclarationLocation.Project p -> p

                            let dependentProjects = ProjectProvider.getDependentProjects dte declProject
                            if dependentProjects |> List.exists (fun x -> x.ProjectFileName = declProject.ProjectFileName)
                            then dependentProjects
                            else declProject :: dependentProjects
                        // The symbol is declared in .NET framework, an external assembly or in a C# project withing the solution.
                        // In order to find all its usages we have to check all F# projects.
                        | None -> dte.ListFSharpProjectsInSolution() |> List.map ProjectProvider.createForProject
    
                    return Some { Word = span, sym
                                  File = file
                                  Project = project
                                  TargetProjects = targetProjects }
                | _ -> return None
            | _ -> return None
        }

    let findReferences() = 
        async {
            let! state = getDocumentState()
            match state with
            | Some { Word = cw, sym; File = file; Project = project; TargetProjects = targetProjects } ->
                let! references = vsLanguageService.FindUsages (cw, file, project, targetProjects) 
                let references = 
                    references
                    |> Option.map (fun (_, _, refs) -> 
                        refs 
                        |> Seq.map (fun symbolUse -> (symbolUse.FileName, symbolUse))
                        |> Seq.groupBy (fst >> Path.GetFullPath)
                        |> Seq.map (fun (_, symbolUses) -> 
                            // Sort symbols by positions
                            symbolUses 
                            |> Seq.map snd 
                            |> Seq.sortBy (fun s -> s.RangeAlternate.StartLine, s.RangeAlternate.StartColumn))
                        |> Seq.concat)
                    |> fun opt -> defaultArg opt Seq.empty
            
                let findResults = FSharpLibraryNode("Find Symbol Results", serviceProvider)
                for reference in references do
                    findResults.AddNode(FSharpLibraryNode(sym.Text, serviceProvider, reference))

                let findService = serviceProvider.GetService<IVsFindSymbol, SVsObjectSearch>()
                let searchCriteria = 
                    VSOBSEARCHCRITERIA2(
                        dwCustom = Constants.FindReferencesResults,
                        eSrchType = VSOBSEARCHTYPE.SO_ENTIREWORD,
                        pIVsNavInfo = (findResults :> IVsNavInfo),
                        grfOptions = uint32 _VSOBSEARCHOPTIONS2.VSOBSO_LISTREFERENCES,
                        szName = sym.Text)

                let guid = ref PkgCmdConst.guidSymbolLibrary
                ErrorHandler.ThrowOnFailure(findService.DoSearch(guid, [| searchCriteria |])) |> ignore
            | _ -> 
                let statusBar = serviceProvider.GetService<IVsStatusbar, SVsStatusbar>()
                statusBar.SetText(Resource.findAllReferenceStatusMessage) |> ignore 
        } |> Async.StartImmediate

    member val IsAdded = false with get, set
    member val NextTarget: IOleCommandTarget = null with get, set

    interface IOleCommandTarget with
        member x.Exec(pguidCmdGroup: byref<Guid>, nCmdId: uint32, nCmdexecopt: uint32, pvaIn: IntPtr, pvaOut: IntPtr) =
            if (pguidCmdGroup = PkgCmdConst.guidBuiltinCmdSet && nCmdId = PkgCmdConst.cmdidFindReferences) then
                findReferences()
            x.NextTarget.Exec(&pguidCmdGroup, nCmdId, nCmdexecopt, pvaIn, pvaOut)

        member x.QueryStatus(pguidCmdGroup: byref<Guid>, cCmds: uint32, prgCmds: OLECMD[], pCmdText: IntPtr) =
            if pguidCmdGroup = PkgCmdConst.guidBuiltinCmdSet && 
                prgCmds |> Seq.exists (fun x -> x.cmdID = PkgCmdConst.cmdidFindReferences) then
                prgCmds.[0].cmdf <- (uint32 OLECMDF.OLECMDF_SUPPORTED) ||| (uint32 OLECMDF.OLECMDF_ENABLED)
                VSConstants.S_OK
            else
                x.NextTarget.QueryStatus(&pguidCmdGroup, cCmds, prgCmds, pCmdText)            
// 
// CSharpCompletionTextEditorExtension.cs
//  
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
// 
// Copyright (c) 2011 Xamarin <http://xamarin.com>
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Linq;
using System.Collections.Generic;

using MonoDevelop.Core;
using MonoDevelop.Debugger;
using MonoDevelop.Ide.Gui;
using MonoDevelop.CodeGeneration;
using MonoDevelop.Ide.Gui.Content;
using MonoDevelop.Ide.CodeCompletion;
using MonoDevelop.Components.Commands;

using MonoDevelop.CSharp.Formatting;

using ICSharpCode.NRefactory6.CSharp.Completion;
using MonoDevelop.Ide.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using MonoDevelop.Ide.Editor.Extension;
using MonoDevelop.Ide.Editor;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;
using System.Xml;

namespace MonoDevelop.CSharp.Completion
{
	public class CSharpCompletionTextEditorExtension : CompletionTextEditorExtension, IDebuggerExpressionResolver
	{
/*		internal protected virtual Mono.TextEditor.TextEditorData TextEditorData {
			get {
				var doc = Document;
				if (doc == null)
					return null;
				return doc.Editor;
			}
		}

		protected virtual IProjectContent ProjectContent {
			get { return Document.GetProjectContext (); }
		}
*/
		SyntaxTree unit;
		static readonly SyntaxTree emptyUnit = CSharpSyntaxTree.ParseText ("");

		SyntaxTree Unit {
			get {
				return unit ?? emptyUnit;
			}
			set {
				unit = value;
			}
		}

		public ParsedDocument ParsedDocument {
			get {
				return DocumentContext.ParsedDocument;
			}
		}
		 
		public MonoDevelop.Projects.Project Project {
			get {
				return DocumentContext.Project;
			}
		}
		
		CSharpFormattingPolicy policy;
		public CSharpFormattingPolicy FormattingPolicy {
			get {
				if (policy == null) {
					IEnumerable<string> types = MonoDevelop.Ide.DesktopService.GetMimeTypeInheritanceChain (MonoDevelop.CSharp.Formatting.CSharpFormatter.MimeType);
					if (DocumentContext.Project != null && DocumentContext.Project.Policies != null) {
						policy = base.DocumentContext.Project.Policies.Get<CSharpFormattingPolicy> (types);
					} else {
						policy = MonoDevelop.Projects.Policies.PolicyService.GetDefaultPolicy<CSharpFormattingPolicy> (types);
					}
				}
				return policy;
			}
		}

		public override string CompletionLanguage {
			get {
				return "C#";
			}
		}

		public CSharpCompletionTextEditorExtension ()
		{
		}

		bool addEventHandlersInInitialization = true;

		/// <summary>
		/// Used in testing environment.
		/// </summary>
		[System.ComponentModel.Browsable(false)]
		public CSharpCompletionTextEditorExtension (MonoDevelop.Ide.Gui.Document doc, bool addEventHandlersInInitialization = true) : this ()
		{
			this.addEventHandlersInInitialization = addEventHandlersInInitialization;
			Initialize (doc.Editor, doc);
		}
		
		protected override void Initialize ()
		{
			base.Initialize ();
//			DocumentContext.DocumentParsed += HandleDocumentParsed;
			var parsedDocument = DocumentContext.ParsedDocument;
			if (parsedDocument != null) {
//				this.Unit = parsedDocument.GetAst<SyntaxTree> ();
//					this.UnresolvedFileCompilation = DocumentContext.Compilation;
//					this.CSharpUnresolvedFile = parsedDocument.ParsedFile as CSharpUnresolvedFile;
//					Editor.CaretPositionChanged += HandlePositionChanged;
			}
			
			if (addEventHandlersInInitialization)
				DocumentContext.DocumentParsed += HandleDocumentParsed; 
		}

		CancellationTokenSource src = new CancellationTokenSource ();

		void StopPositionChangedTask ()
		{
			src.Cancel ();
			src = new CancellationTokenSource ();
		}
			
		[CommandUpdateHandler (CodeGenerationCommands.ShowCodeGenerationWindow)]
		public void CheckShowCodeGenerationWindow (CommandInfo info)
		{
			info.Enabled = Editor != null && DocumentContext.GetContent<ICompletionWidget> () != null;
		}

		[CommandHandler (CodeGenerationCommands.ShowCodeGenerationWindow)]
		public void ShowCodeGenerationWindow ()
		{
			var completionWidget = DocumentContext.GetContent<ICompletionWidget> ();
			if (completionWidget == null)
				return;
			CodeCompletionContext completionContext = completionWidget.CreateCodeCompletionContext (Editor.CaretOffset);
			GenerateCodeWindow.ShowIfValid (Editor, DocumentContext, completionContext);
		}

		public override void Dispose ()
		{
			DocumentContext.DocumentParsed -= HandleDocumentParsed;
			if (validTypeSystemSegmentTree != null) {
				validTypeSystemSegmentTree.RemoveListener ();
				validTypeSystemSegmentTree = null;
			}

			base.Dispose ();
		}

		async void HandleDocumentParsed (object sender, EventArgs e)
		{
			var parsedDocument = DocumentContext.ParsedDocument;
			if (parsedDocument == null) 
				return;
			var semanticModel = parsedDocument.GetAst<SemanticModel> ();
			if (semanticModel == null) 
				return;
			var newTree = TypeSystemSegmentTree.Create (Editor, DocumentContext, semanticModel);

			if (validTypeSystemSegmentTree != null)
				validTypeSystemSegmentTree.RemoveListener ();
			validTypeSystemSegmentTree = newTree;
			newTree.InstallListener (Editor);

			if (TypeSegmentTreeUpdated != null)
				TypeSegmentTreeUpdated (this, EventArgs.Empty);
		}

		public event EventHandler TypeSegmentTreeUpdated;

		public void UpdateParsedDocument ()
		{
			HandleDocumentParsed (null, null);
		}
		
		public override bool KeyPress (KeyDescriptor descriptor)
		{
			bool result = base.KeyPress (descriptor);
			
			if (/*EnableParameterInsight &&*/ (descriptor.KeyChar == ',' || descriptor.KeyChar == ')') && CanRunParameterCompletionCommand ())
				base.RunParameterCompletionCommand ();
			
//			if (IsInsideComment ())
//				ParameterInformationWindowManager.HideWindow (CompletionWidget);
			return result;
		}
		
		public override Task<ICompletionDataList> HandleCodeCompletionAsync (CodeCompletionContext completionContext, char completionChar, CancellationToken token = default(CancellationToken))
		{
//			if (!EnableCodeCompletion)
//				return null;
			if (!EnableAutoCodeCompletion && char.IsLetter (completionChar))
				return null;

			//	var timer = Counters.ResolveTime.BeginTiming ();
			try {
				int triggerWordLength = 0;
				if (char.IsLetterOrDigit (completionChar) || completionChar == '_') {
					if (completionContext.TriggerOffset > 1 && char.IsLetterOrDigit (Editor.GetCharAt (completionContext.TriggerOffset - 2)))
						return null;
					triggerWordLength = 1;
				}
				return InternalHandleCodeCompletion (completionContext, completionChar, false, triggerWordLength, token);
			} catch (Exception e) {
				LoggingService.LogError ("Unexpected code completion exception." + Environment.NewLine + 
					"FileName: " + DocumentContext.Name + Environment.NewLine + 
					"Position: line=" + completionContext.TriggerLine + " col=" + completionContext.TriggerLineOffset + Environment.NewLine + 
					"Line text: " + Editor.GetLineText (completionContext.TriggerLine), 
					e);
				return null;
			} finally {
				//			if (timer != null)
				//				timer.Dispose ();
			}
		}

		class CSharpCompletionDataList : CompletionDataList
		{
		}

		interface IListData
		{
			CSharpCompletionDataList List { get; set; }
		}
		
		async Task<ICompletionDataList> InternalHandleCodeCompletion (CodeCompletionContext completionContext, char completionChar, bool ctrlSpace, int triggerWordLength, CancellationToken token)
		{
			if (Editor.EditMode != MonoDevelop.Ide.Editor.EditMode.Edit)
				return null;
//			var data = Editor;
//			if (data.CurrentMode is TextLinkEditMode) {
//				if (((TextLinkEditMode)data.CurrentMode).TextLinkMode == TextLinkMode.EditIdentifier)
//					return null;
//			}
			var offset = Editor.CaretOffset;

			var list = new CSharpCompletionDataList ();
			list.TriggerWordLength = triggerWordLength;
			try {
				var analysisDocument = DocumentContext.AnalysisDocument;
				if (analysisDocument == null)
					return null;
				
				var parsedDocument = DocumentContext.UpdateParseDocument ();
				var semanticModel = parsedDocument.GetAst<SemanticModel> ();
				var engine = new CompletionEngine (TypeSystemService.Workspace, new RoslynCodeCompletionFactory (this));
				var completionResult = engine.GetCompletionData (analysisDocument, semanticModel, offset, ctrlSpace, token);
				if (completionResult == CompletionResult.Empty)
					return null;
				foreach (var symbol in completionResult) {
					list.Add (symbol); 
				}
				MonoDevelop.Ide.CodeTemplates.CodeTemplateService.AddCompletionDataForMime ("text/x-csharp", list);
				list.AutoCompleteEmptyMatch = completionResult.AutoCompleteEmptyMatch;
				// list.AutoCompleteEmptyMatchOnCurlyBrace = completionResult.AutoCompleteEmptyMatchOnCurlyBracket;
				list.AutoSelect = completionResult.AutoSelect;
				list.DefaultCompletionString = completionResult.DefaultCompletionString;
				// list.CloseOnSquareBrackets = completionResult.CloseOnSquareBrackets;
				if (ctrlSpace)
					list.AutoCompleteUniqueMatch = true;
			} catch (TaskCanceledException e) {
				return null;
			} catch (Exception e) {
				LoggingService.LogError ("Error while getting C# recommendations", e); 
			}


//			list.Resolver = CSharpUnresolvedFile != null ? CSharpUnresolvedFile.GetResolver (UnresolvedFileCompilation, Document.Editor.Caret.Location) : new CSharpResolver (Compilation);
//			var ctx = CreateTypeResolveContext ();
//			if (ctx == null)
//				return null;
			//			var completionDataFactory = new CompletionDataFactory (this, new CSharpResolver (ctx));
//			if (MDRefactoringCtx == null) {
//				src.Cancel ();
//				MDRefactoringCtx = MDRefactoringContext.Create (Document, Document.Editor.Caret.Location);
//			}
//
//			var engine = new MonoCSharpCompletionEngine (
//				this,
//				data.Document,
//				CreateContextProvider (),
//				completionDataFactory,
//				Document.GetProjectContext (),
//				ctx
//			);
//			completionDataFactory.Engine = engine;
//			engine.AutomaticallyAddImports = AddImportedItemsToCompletionList.Value;
//			engine.IncludeKeywordsInCompletionList = EnableAutoCodeCompletion || IncludeKeywordsInCompletionList.Value;
//			engine.CompletionEngineCache = cache;
//			if (FilterCompletionListByEditorBrowsable) {
//				engine.EditorBrowsableBehavior = IncludeEditorBrowsableAdvancedMembers ? EditorBrowsableBehavior.IncludeAdvanced : EditorBrowsableBehavior.Normal;
//			} else {
//				engine.EditorBrowsableBehavior = EditorBrowsableBehavior.Ignore;
//			}
//			if (Document.HasProject && MonoDevelop.Ide.IdeApp.IsInitialized) {
//				var configuration = Document.Project.GetConfiguration (MonoDevelop.Ide.IdeApp.Workspace.ActiveConfiguration) as DotNetProjectConfiguration;
//				var par = configuration != null ? configuration.CompilationParameters as CSharpCompilerParameters : null;
//				if (par != null)
//					engine.LanguageVersion = MonoDevelop.CSharp.Parser.TypeSystemParser.ConvertLanguageVersion (par.LangVersion);
//			}
//
//			engine.FormattingPolicy = FormattingPolicy.CreateOptions ();
//			engine.EolMarker = data.EolMarker;
//			engine.IndentString = data.Options.IndentationString;
//			try {
//				foreach (var cd in engine.GetCompletionData (completionContext.TriggerOffset, ctrlSpace)) {
//					list.Add (cd);
//					if (cd is IListData)
//						((IListData)cd).List = list;
//				}
//			} catch (Exception e) {
//				LoggingService.LogError ("Error while getting completion data.", e);
//			}
			return (ICompletionDataList)list;
		}
		
		public override ICompletionDataList CodeCompletionCommand (CodeCompletionContext completionContext)
		{
			int triggerWordLength = 0;
			char ch = completionContext.TriggerOffset > 0 ? Editor.GetCharAt (completionContext.TriggerOffset - 1) : '\0';
			return InternalHandleCodeCompletion (completionContext, ch, true, triggerWordLength, default(CancellationToken)).Result;
		}

		static bool HasAllUsedParameters (IParameterHintingData provider, string[] list)
		{
			if (provider == null || list == null)
				return true;
			int pc = provider.ParameterCount;
			foreach (var usedParam in list) {
				bool found = false;
				for (int m = 0; m < pc; m++) {
					if (usedParam == provider.GetParameterName (m)){
						found = true;
						break;
					}
				}
				if (!found)
					return false;
			}
			return true;
		}
		
		public override int GuessBestMethodOverload (ParameterHintingResult provider, int currentOverload)
		{
			var analysisDocument = DocumentContext.AnalysisDocument;
			if (analysisDocument == null)
				return -1;
			var result = ICSharpCode.NRefactory6.CSharp.ParameterUtil.GetCurrentParameterIndex (analysisDocument, provider.StartOffset, Editor.CaretOffset).Result;
			var cparam = result.ParameterIndex;
			var list = result.UsedNamespaceParameters;
			if (cparam > provider[currentOverload].ParameterCount && !provider[currentOverload].IsParameterListAllowed || !HasAllUsedParameters (provider[currentOverload], list)) {
				// Look for an overload which has more parameters
				int bestOverload = -1;
				int bestParamCount = int.MaxValue;
				for (int n = 0; n < provider.Count; n++) {
					int pc = provider[n].ParameterCount;
					if (pc < bestParamCount && pc >= cparam) {

						if (HasAllUsedParameters (provider[n], list)) {
							bestOverload = n;
							bestParamCount = pc;
						}
					}


				}
				if (bestOverload == -1) {
					for (int n=0; n<provider.Count; n++) {
						if (provider[n].IsParameterListAllowed && HasAllUsedParameters (provider[n], list)) {
							bestOverload = n;
							break;
						}
					}
				}
				return bestOverload;
			}
			return -1;
		}

		
//		static bool ContainsPublicConstructors (ITypeDefinition t)
//		{
//			if (t.Methods.Count (m => m.IsConstructor) == 0)
//				return true;
//			return t.Methods.Any (m => m.IsConstructor && m.IsPublic);
//		}


//			CompletionDataList result = new ProjectDomCompletionDataList ();
//			// "var o = new " needs special treatment.
//			if (returnType == null && returnTypeUnresolved != null && returnTypeUnresolved.FullName == "var")
//				returnType = returnTypeUnresolved = DomReturnType.Object;
//
//			//	ExpressionContext.TypeExpressionContext tce = context as ExpressionContext.TypeExpressionContext;
//
//			CompletionDataCollector col = new CompletionDataCollector (this, dom, result, Document.CompilationUnit, callingType, location);
//			IType type = null;
//			if (returnType != null)
//				type = dom.GetType (returnType);
//			if (type == null)
//				type = dom.SearchType (Document.CompilationUnit, callingType, location, returnTypeUnresolved);
//			
//			// special handling for nullable types: Bug 674516 - new completion for nullables should not include "Nullable"
//			if (type is InstantiatedType && ((InstantiatedType)type).UninstantiatedType.FullName == "System.Nullable" && ((InstantiatedType)type).GenericParameters.Count == 1) {
//				var genericParameter = ((InstantiatedType)type).GenericParameters [0];
//				returnType = returnTypeUnresolved = Document.CompilationUnit.ShortenTypeName (genericParameter, location);
//				type = dom.SearchType (Document.CompilationUnit, callingType, location, genericParameter);
//			}
//			
//			if (type == null || !(type.IsAbstract || type.ClassType == ClassType.Interface)) {
//				if (type == null || type.ConstructorCount == 0 || type.Methods.Any (c => c.IsConstructor && c.IsAccessibleFrom (dom, callingType, type, callingType != null && dom.GetInheritanceTree (callingType).Any (x => x.FullName == type.FullName)))) {
//					if (returnTypeUnresolved != null) {
//						col.FullyQualify = true;
//						CompletionData unresovedCompletionData = col.Add (returnTypeUnresolved);
//						col.FullyQualify = false;
//						// don't set default completion string for arrays, since it interferes with: 
//						// string[] arr = new string[] vs new { "a"}
//						if (returnTypeUnresolved.ArrayDimensions == 0)
//							result.DefaultCompletionString = StripGenerics (unresovedCompletionData.CompletionText);
//					} else {
//						CompletionData unresovedCompletionData = col.Add (returnType);
//						if (returnType.ArrayDimensions == 0)
//							result.DefaultCompletionString = StripGenerics (unresovedCompletionData.CompletionText);
//					}
//				}
//			}
//			
//			//				if (tce != null && tce.Type != null) {
//			//					result.DefaultCompletionString = StripGenerics (col.AddCompletionData (result, tce.Type).CompletionString);
//			//				} 
//			//			else {
//			//			}
//			if (type == null)
//				return result;
//			HashSet<string > usedNamespaces = new HashSet<string> (GetUsedNamespaces ());
//			if (type.FullName == DomReturnType.Object.FullName) 
//				AddPrimitiveTypes (col);
//			
//			foreach (IType curType in dom.GetSubclasses (type)) {
//				if (context != null && context.FilterEntry (curType))
//					continue;
//				if ((curType.TypeModifier & TypeModifier.HasOnlyHiddenConstructors) == TypeModifier.HasOnlyHiddenConstructors)
//					continue;
//				if (usedNamespaces.Contains (curType.Namespace)) {
//					if (curType.ConstructorCount > 0) {
//						if (!(curType.Methods.Any (c => c.IsConstructor && c.IsAccessibleFrom (dom, curType, callingType, callingType != null && dom.GetInheritanceTree (callingType).Any (x => x.FullName == curType.FullName)))))
//							continue;
//					}
//					col.Add (curType);
//				} else {
//					string nsName = curType.Namespace;
//					int idx = nsName.IndexOf ('.');
//					if (idx >= 0)
//						nsName = nsName.Substring (0, idx);
//					col.Add (new Namespace (nsName));
//				}
//			}
//			
//			// add aliases
//			if (returnType != null) {
//				foreach (IUsing u in Document.CompilationUnit.Usings) {
//					foreach (KeyValuePair<string, IReturnType> alias in u.Aliases) {
//						if (alias.Value.ToInvariantString () == returnType.ToInvariantString ())
//							result.Add (alias.Key, "md-class");
//					}
//				}
//			}
//			
//			return result;
//		}
		
//		IEnumerable<ICompletionData> GetDefineCompletionData ()
//		{
//			if (Document.Project == null)
//				yield break;
//			
//			var symbols = new Dictionary<string, string> ();
//			var cp = new ProjectDomCompletionDataList ();
//			foreach (DotNetProjectConfiguration conf in Document.Project.Configurations) {
//				var cparams = conf.CompilationParameters as CSharpCompilerParameters;
//				if (cparams != null) {
//					string[] syms = cparams.DefineSymbols.Split (';');
//					foreach (string s in syms) {
//						string ss = s.Trim ();
//						if (ss.Length > 0 && !symbols.ContainsKey (ss)) {
//							symbols [ss] = ss;
//							yield return factory.CreateLiteralCompletionData (ss);
//						}
//					}
//				}
//			}
//		}
		

		public override async Task<ParameterHintingResult> HandleParameterCompletionAsync (CodeCompletionContext completionContext, char completionChar, CancellationToken token = default(CancellationToken))
		{
			var data = Editor;
			if (completionChar != '(' && completionChar != ',')
				return null;
			if (Editor.EditMode != MonoDevelop.Ide.Editor.EditMode.Edit)
				return null;
			var offset = Editor.CaretOffset;

			if (completionChar != '(' && completionChar != ',')
				return null;

			try {
				var parsedDocument = DocumentContext.ParsedDocument;
				if (parsedDocument == null)
					return null;
				var semanticModel = DocumentContext.ParsedDocument.GetAst<SemanticModel> ();
				var engine = new ParameterHintingEngine (TypeSystemService.Workspace, new RoslynParameterHintingFactory ());
				return engine.GetParameterDataProvider (DocumentContext.AnalysisDocument, semanticModel, offset, token);
			} catch (Exception e) {
				LoggingService.LogError ("Unexpected parameter completion exception." + Environment.NewLine + 
					"FileName: " + DocumentContext.Name + Environment.NewLine + 
					"Position: line=" + completionContext.TriggerLine + " col=" + completionContext.TriggerLineOffset + Environment.NewLine + 
					"Line text: " + Editor.GetLineText (completionContext.TriggerLine), 
					e);
			}
			return null;
		}
		
//		List<string> GetUsedNamespaces ()
//		{
//			var scope = CSharpUnresolvedFile.GetUsingScope (document.Editor.Caret.Location);
//			var result = new List<string> ();
//			while (scope != null) {
//				result.Add (scope.NamespaceName);
//				var ctx = CSharpUnresolvedFile.GetResolver (Document.Compilation, scope.Region.Begin);
//				foreach (var u in scope.Usings) {
//					var ns = u.ResolveNamespace (ctx);
//					if (ns == null)
//						continue;
//					result.Add (ns.FullName);
//				}
//				scope = scope.Parent;
//			}
//			return result;
//		}

		public override int GetCurrentParameterIndex (int startOffset)
		{
			var analysisDocument = DocumentContext.AnalysisDocument;
			if (analysisDocument == null)
				return -1;
 			var result = ICSharpCode.NRefactory6.CSharp.ParameterUtil.GetCurrentParameterIndex (analysisDocument, startOffset, Editor.CaretOffset).Result;
			return result.ParameterIndex;
		}

/*
		#region ICompletionDataFactory implementation
		internal class CompletionDataFactory : ICompletionDataFactory
		{
			internal readonly CSharpCompletionTextEditorExtension ext;
//			readonly CSharpResolver state;
			readonly TypeSystemAstBuilder builder;

			public CSharpCompletionEngine Engine {
				get;
				set;
			}

			public CompletionDataFactory (CSharpCompletionTextEditorExtension ext, CSharpResolver state)
			{
//				this.state = state;
				if (state != null)
					builder = new TypeSystemAstBuilder(state);
				this.ext = ext;
			}
			
			ICompletionData ICompletionDataFactory.CreateEntityCompletionData (IEntity entity)
			{
				return new MemberCompletionData (this, entity, OutputFlags.IncludeGenerics | OutputFlags.HideArrayBrackets | OutputFlags.IncludeParameterName) {
					HideExtensionParameter = true
				};
			}

			class GenericTooltipCompletionData : CompletionData, IListData
			{
				readonly Func<CSharpCompletionDataList, bool, TooltipInformation> tooltipFunc;

				#region IListData implementation

				CSharpCompletionDataList list;
				public CSharpCompletionDataList List {
					get {
						return list;
					}
					set {
						list = value;
						if (overloads != null) {
							foreach (var overload in overloads.Skip (1)) {
								var ld = overload as IListData;
								if (ld != null)
									ld.List = list;
							}
						}
					}
				}

				#endregion

				public GenericTooltipCompletionData (Func<CSharpCompletionDataList, bool, TooltipInformation> tooltipFunc, string text, string icon) : base (text, icon)
				{
					this.tooltipFunc = tooltipFunc;
				}

				public GenericTooltipCompletionData (Func<CSharpCompletionDataList, bool, TooltipInformation> tooltipFunc, string text, string icon, string description, string completionText) : base (text, icon, description, completionText)
				{
					this.tooltipFunc = tooltipFunc;
				}

				public override TooltipInformation CreateTooltipInformation (bool smartWrap)
				{
					return tooltipFunc != null ? tooltipFunc (List, smartWrap) : new TooltipInformation ();
				}

				protected List<ICSharpCode.NRefactory6.CSharp.Completion.ICompletionData> overloads;
				public override bool HasOverloads {
					get { return overloads != null && overloads.Count > 0; }
				}

				public override IEnumerable<ICSharpCode.NRefactory6.CSharp.Completion.ICompletionData> OverloadedData {
					get {
						return overloads;
					}
				}

				public override void AddOverload (ICSharpCode.NRefactory.Completion.ICompletionData data)
				{
					if (overloads == null) {
						overloads = new List<ICompletionData> ();
						overloads.Add (this);
					}
					overloads.Add (data);
				}

				public override void InsertCompletionText (CompletionListWindow window, ref KeyActions ka, KeyDescriptor descriptor)
				{
					var currentWord = GetCurrentWord (window);
					if (CompletionText == "new()" && descriptor.KeyChar == '(') {
						window.CompletionWidget.SetCompletionText (window.CodeCompletionContext, currentWord, "new");
					} else {
						window.CompletionWidget.SetCompletionText (window.CodeCompletionContext, currentWord, CompletionText);
					}
				}

			}

			class LazyGenericTooltipCompletionData : GenericTooltipCompletionData
			{
				Lazy<string> displayText;
				public override string DisplayText {
					get {
						return displayText.Value;
					}
				}

				public override string CompletionText {
					get {
						return displayText.Value;
					}
				}

				public LazyGenericTooltipCompletionData (Func<CSharpCompletionDataList, bool, TooltipInformation> tooltipFunc, Lazy<string> displayText, string icon) : base (tooltipFunc, null, icon)
				{
					this.displayText = displayText;
				}
			}

			class TypeCompletionData : LazyGenericTooltipCompletionData, IListData
			{
				IType type;
				CSharpCompletionTextEditorExtension ext;
				CSharpUnresolvedFile file;
				ICompilation compilation;
//				CSharpResolver resolver;

				string IdString {
					get {
						return DisplayText + type.TypeParameterCount;
					}
				}

				public override string CompletionText {
					get {
						if (type.TypeParameterCount > 0 && !type.IsParameterized)
							return type.Name;
						return base.CompletionText;
					}
				}

				public override TooltipInformation CreateTooltipInformation (bool smartWrap)
				{
					var def = type.GetDefinition ();
					var result = def != null ? MemberCompletionData.CreateTooltipInformation (compilation, file, List.Resolver, ext.Editor, ext.FormattingPolicy, def, smartWrap)  : new TooltipInformation ();
					if (ConflictingTypes != null) {
						var conflicts = new StringBuilder ();
						var sig = new SignatureMarkupCreator (List.Resolver, ext.FormattingPolicy.CreateOptions ());
						for (int i = 0; i < ConflictingTypes.Count; i++) {
							var ct = ConflictingTypes[i];
							if (i > 0)
								conflicts.AppendLine (",");
//							if ((i + 1) % 5 == 0)
//								conflicts.Append (Environment.NewLine + "\t");
							conflicts.Append (sig.GetTypeReferenceString (((TypeCompletionData)ct).type));
						}
						result.AddCategory ("Type Conflicts", conflicts.ToString ());
					}
					return result;
				}

				public TypeCompletionData (IType type, CSharpCompletionTextEditorExtension ext, Lazy<string> displayText, string icon, bool addConstructors) : base (null, displayText, icon)
				{
					this.type = type;
					this.ext = ext;
					this.file = ext.CSharpUnresolvedFile;
					this.compilation = ext.UnresolvedFileCompilation;

				}

				Dictionary<string, ICSharpCode.NRefactory.Completion.ICompletionData> addedDatas = new Dictionary<string, ICSharpCode.NRefactory.Completion.ICompletionData> ();

				List<ICompletionData> ConflictingTypes = null;

				public override void AddOverload (ICSharpCode.NRefactory.Completion.ICompletionData data)
				{
					if (overloads == null)
						addedDatas [IdString] = this;

					if (data is TypeCompletionData) {
						string id = ((TypeCompletionData)data).IdString;
						ICompletionData oldData;
						if (addedDatas.TryGetValue (id, out oldData)) {
							var old = (TypeCompletionData)oldData;
							if (old.ConflictingTypes == null)
								old.ConflictingTypes = new List<ICompletionData> ();
							old.ConflictingTypes.Add (data);
							return;
						}
						addedDatas [id] = data;
					}

					base.AddOverload (data);
				}

			}

			ICompletionData ICompletionDataFactory.CreateEntityCompletionData (IEntity entity, string text)
			{
				return new GenericTooltipCompletionData ((list, sw) => MemberCompletionData.CreateTooltipInformation (ext, list.Resolver, entity, sw), text, entity.GetStockIcon ());
			}

			ICompletionData ICompletionDataFactory.CreateTypeCompletionData (IType type, bool showFullName, bool isInAttributeContext, bool addConstructors)
			{
				if (addConstructors) {
					ICompletionData constructorResult = null;
					foreach (var ctor in type.GetConstructors ()) {
						if (constructorResult != null) {
							constructorResult.AddOverload (((ICompletionDataFactory)this).CreateEntityCompletionData (ctor));
						} else {
							constructorResult = ((ICompletionDataFactory)this).CreateEntityCompletionData (ctor);
						}
					}
					return constructorResult;
				}

				Lazy<string> displayText = new Lazy<string> (delegate {
					string name = showFullName ? builder.ConvertType(type).ToString() : type.Name; 
					if (isInAttributeContext && name.EndsWith("Attribute") && name.Length > "Attribute".Length) {
						name = name.Substring(0, name.Length - "Attribute".Length);
					}
					return name;
				});

				var result = new TypeCompletionData (type, ext,
					displayText, 
					type.GetStockIcon (),
					addConstructors);
				return result;
			}

			ICompletionData ICompletionDataFactory.CreateMemberCompletionData(IType type, IEntity member)
			{
				Lazy<string> displayText = new Lazy<string> (delegate {
					string name = builder.ConvertType(type).ToString(); 
					return name + "."+ member.Name;
				});

				var result = new LazyGenericTooltipCompletionData (
					(List, sw) => new TooltipInformation (), 
					displayText, 
					member.GetStockIcon ());
				return result;
			}


			ICompletionData ICompletionDataFactory.CreateLiteralCompletionData (string title, string description, string insertText)
			{
				return new GenericTooltipCompletionData ((list, smartWrap) => {
					var sig = new SignatureMarkupCreator (list.Resolver, ext.FormattingPolicy.CreateOptions ());
					sig.BreakLineAfterReturnType = smartWrap;
					return sig.GetKeywordTooltip (title, null);
				}, title, "md-keyword", description, insertText ?? title);
			}

			class XmlDocCompletionData : CompletionData, IListData
			{
				readonly CSharpCompletionTextEditorExtension ext;
				readonly string title;

				#region IListData implementation

				CSharpCompletionDataList list;
				public CSharpCompletionDataList List {
					get {
						return list;
					}
					set {
						list = value;
					}
				}

				#endregion

				public XmlDocCompletionData (CSharpCompletionTextEditorExtension ext, string title, string description, string insertText) : base (title, "md-keyword", description, insertText ?? title)
				{
					this.ext = ext;
					this.title = title;
				}

				public override TooltipInformation CreateTooltipInformation (bool smartWrap)
				{
					var sig = new SignatureMarkupCreator (List.Resolver, ext.FormattingPolicy.CreateOptions ());
					sig.BreakLineAfterReturnType = smartWrap;
					return sig.GetKeywordTooltip (title, null);
				}



				public override void InsertCompletionText (CompletionListWindow window, ref KeyActions ka, KeyDescriptor descriptor)
				{
					var currentWord = GetCurrentWord (window);
					var text = CompletionText;
					if (descriptor.KeyChar != '>')
						text += ">";
					window.CompletionWidget.SetCompletionText (window.CodeCompletionContext, currentWord, text);
				}
			}

			ICompletionData ICompletionDataFactory.CreateXmlDocCompletionData (string title, string description, string insertText)
			{
				return new XmlDocCompletionData (ext, title, description, insertText);
			}

			ICompletionData ICompletionDataFactory.CreateNamespaceCompletionData (INamespace name)
			{
				return new CompletionData (name.Name, AstStockIcons.Namespace, "", CSharpAmbience.FilterName (name.Name));
			}

			ICompletionData ICompletionDataFactory.CreateVariableCompletionData (IVariable variable)
			{
				return new VariableCompletionData (ext, variable);
			}

			ICompletionData ICompletionDataFactory.CreateVariableCompletionData (ITypeParameter parameter)
			{
				return new CompletionData (parameter.Name, parameter.GetStockIcon ());
			}

			ICompletionData ICompletionDataFactory.CreateEventCreationCompletionData (string varName, IType delegateType, IEvent evt, string parameterDefinition, IUnresolvedMember currentMember, IUnresolvedTypeDefinition currentType)
			{
				return new EventCreationCompletionData (ext, varName, delegateType, evt, parameterDefinition, currentMember, currentType);
			}
			
			ICompletionData ICompletionDataFactory.CreateNewOverrideCompletionData (int declarationBegin, IUnresolvedTypeDefinition type, IMember m)
			{
				return new NewOverrideCompletionData (ext, declarationBegin, type, m);
			}
			ICompletionData ICompletionDataFactory.CreateNewPartialCompletionData (int declarationBegin, IUnresolvedTypeDefinition type, IUnresolvedMember m)
			{
				var ctx = ext.CSharpUnresolvedFile.GetTypeResolveContext (ext.UnresolvedFileCompilation, ext.Editor.CaretLocation);
				return new NewOverrideCompletionData (ext, declarationBegin, type, m.CreateResolved (ctx));
			}
			IEnumerable<ICompletionData> ICompletionDataFactory.CreateCodeTemplateCompletionData ()
			{
				var result = new CompletionDataList ();
				if (EnableAutoCodeCompletion || IncludeCodeSnippetsInCompletionList.Value) {
					CodeTemplateService.AddCompletionDataForMime ("text/x-csharp", result);
				}
				return result;
			}
			
			IEnumerable<ICompletionData> ICompletionDataFactory.CreatePreProcessorDefinesCompletionData ()
			{
				var project = ext.DocumentContext.Project;
				if (project == null)
					yield break;
				var configuration = project.GetConfiguration (MonoDevelop.Ide.IdeApp.Workspace.ActiveConfiguration) as DotNetProjectConfiguration;
				if (configuration == null)
					yield break;
				foreach (var define in configuration.GetDefineSymbols ())
					yield return new CompletionData (define, "md-keyword");
					
			}

			class FormatItemCompletionData : CompletionData
			{
				string format;
				string description;
				object example;

				public FormatItemCompletionData (string format, string description, object example)
				{
					this.format = format;
					this.description = description;
					this.example = example;
				}

				
				public override string DisplayText {
					get {
						return format;
					}
				}
				public override string GetDisplayDescription (bool isSelected)
				{
					return "- <span foreground=\"darkgray\" size='small'>" + description + "</span>";
				}


				string rightSideDescription = null;
				public override string GetRightSideDescription (bool isSelected)
				{
					if (rightSideDescription == null) {
						try {
							rightSideDescription = "<span size='small'>" + string.Format ("{0:" +format +"}", example) +"</span>";
						} catch (Exception e) {
							rightSideDescription = "";
							LoggingService.LogError ("Format error.", e);
						}
					}
					return rightSideDescription;
				}

				public override string CompletionText {
					get {
						return format;
					}
				}

				public override int CompareTo (object obj)
				{
					return 0;
				}
			}


			ICompletionData ICompletionDataFactory.CreateFormatItemCompletionData(string format, string description, object example)
			{
				return new FormatItemCompletionData (format, description, example);
			}


			class ImportSymbolCompletionData : CompletionData, IEntityCompletionData
			{
				readonly IType type;
				readonly bool useFullName;
				readonly CSharpCompletionTextEditorExtension ext;
				public IType Type {
					get { return this.type; }
				}

				public ImportSymbolCompletionData (CSharpCompletionTextEditorExtension ext, bool useFullName, IType type, bool addConstructors)
				{
					this.ext = ext;
					this.useFullName = useFullName;
					this.type = type;
					this.DisplayFlags |= ICSharpCode.NRefactory.Completion.DisplayFlags.IsImportCompletion;
				}

				public override TooltipInformation CreateTooltipInformation (bool smartWrap)
				{
					return MemberCompletionData.CreateTooltipInformation (ext, null, type.GetDefinition (), smartWrap);
				}

				bool initialized = false;
				bool generateUsing, insertNamespace;

				void Initialize ()
				{
					if (initialized)
						return;
					initialized = true;
					if (string.IsNullOrEmpty (type.Namespace)) 
						return;
					generateUsing = !useFullName;
					insertNamespace = useFullName;
				}

				#region IActionCompletionData implementation
				public override void InsertCompletionText (CompletionListWindow window, ref KeyActions ka, KeyDescriptor descriptor)
				{
					Initialize ();
					var doc = ext.DocumentContext;
					using (var undo = ext.Editor.OpenUndoGroup ()) {
						string text = insertNamespace ? type.Namespace + "." + type.Name : type.Name;
						if (text != GetCurrentWord (window)) {
							if (window.WasShiftPressed && generateUsing) 
								text = type.Namespace + "." + text;
							window.CompletionWidget.SetCompletionText (window.CodeCompletionContext, GetCurrentWord (window), text);
						}
					
						if (!window.WasShiftPressed && generateUsing) {
							var generator = CodeGenerator.CreateGenerator (ext.Editor, doc);
							if (generator != null) {
								generator.AddGlobalNamespaceImport (ext.Editor, doc, type.Namespace);
							}
						}
					}
					ka |= KeyActions.Ignore;
				}
				#endregion

				#region ICompletionData implementation
				public override IconId Icon {
					get {
						return type.GetStockIcon ();
					}
				}
				
				public override string DisplayText {
					get {
						return type.Name;
					}
				}

				static string GetDefaultDisplaySelection (string description, bool isSelected)
				{
					if (!isSelected)
						return "<span foreground=\"darkgray\">" + description + "</span>";
					return description;
				}

				string displayDescription = null;
				public override string GetDisplayDescription (bool isSelected)
				{
					if (displayDescription == null) {
						Initialize ();
						if (generateUsing || insertNamespace) {
							displayDescription = string.Format (GettextCatalog.GetString ("(from '{0}')"), type.Namespace);
						} else {
							displayDescription = "";
						}
					}
					return GetDefaultDisplaySelection (displayDescription, isSelected);
				}

				public override string Description {
					get {
						return type.Namespace;
					}
				}

				public override string CompletionText {
					get {
						return type.Name;
					}
				}
				#endregion


				List<CompletionData> overloads;

				public override IEnumerable<ICompletionData> OverloadedData {
					get {
						yield return this;
						if (overloads == null)
							yield break;
						foreach (var overload in overloads)
							yield return overload;
					}
				}

				public override bool HasOverloads {
					get { return overloads != null && overloads.Count > 0; }
				}

				public override void AddOverload (ICSharpCode.NRefactory.Completion.ICompletionData data)
				{
					AddOverload ((ImportSymbolCompletionData)data);
				}

				void AddOverload (ImportSymbolCompletionData overload)
				{
					if (overloads == null)
						overloads = new List<CompletionData> ();
					overloads.Add (overload);
				}

				IEntity IEntityCompletionData.Entity {
					get {
						return type.GetDefinition ();
					}
				}
			}


			ICompletionData ICompletionDataFactory.CreateImportCompletionData(IType type, bool useFullName, bool addConstructors)
			{
				return new ImportSymbolCompletionData (ext, useFullName, type, addConstructors);
			}

		}
		#endregion
*/
		

		#region IDebuggerExpressionResolver implementation

//		static string GetIdentifierName (IReadonlyTextDocument editor, ICSharpCode.NRefactory.CSharp.Identifier id, out int startOffset)
//		{
//			startOffset = editor.LocationToOffset (id.StartLocation.Line, id.StartLocation.Column);
//
//			return editor.GetTextBetween (id.StartLocation, id.EndLocation);
//		}

//		internal static string ResolveExpression (IReadonlyTextDocument editor, ResolveResult result, ICSharpCode.NRefactory.CSharp.AstNode node, out int startOffset)
//		{
//			//Console.WriteLine ("result is a {0}", result.GetType ().Name);
//			startOffset = -1;
//
//			if (result is NamespaceResolveResult ||
//				result is ConversionResolveResult ||
//				result is ConstantResolveResult ||
//				result is ForEachResolveResult ||
//				result is TypeIsResolveResult ||
//				result is TypeOfResolveResult ||
//				result is ErrorResolveResult)
//				return null;
//
//			if (result.IsCompileTimeConstant)
//				return null;
//
//			startOffset = editor.LocationToOffset (node.StartLocation.Line, node.StartLocation.Column);
//
//			if (result is InvocationResolveResult) {
//				var ir = (InvocationResolveResult) result;
//				if (ir.Member.Name == ".ctor") {
//					// if the user is hovering over something like "new Abc (...)", we want to show them type information for Abc
//					return ir.Member.DeclaringType.FullName;
//				}
//
//				// do not support general method invocation for tooltips because it could cause side-effects
//				return null;
//			} else if (result is LocalResolveResult) {
//				if (node is ICSharpCode.NRefactory.CSharp.ParameterDeclaration) {
//					// user is hovering over a method parameter, but we don't want to include the parameter type
//					var param = (ICSharpCode.NRefactory.CSharp.ParameterDeclaration) node;
//
//					return GetIdentifierName (editor, param.NameToken, out startOffset);
//				}
//
//				if (node is ICSharpCode.NRefactory.CSharp.VariableInitializer) {
//					// user is hovering over something like "int fubar = 5;", but we don't want the expression to include the " = 5"
//					var variable = (ICSharpCode.NRefactory.CSharp.VariableInitializer) node;
//
//					return GetIdentifierName (editor, variable.NameToken, out startOffset);
//				}
//			} else if (result is MemberResolveResult) {
//				var mr = (MemberResolveResult) result;
//
//				if (node is ICSharpCode.NRefactory.CSharp.PropertyDeclaration) {
//					var prop = (ICSharpCode.NRefactory.CSharp.PropertyDeclaration) node;
//					var name = GetIdentifierName (editor, prop.NameToken, out startOffset);
//
//					// if the property is static, then we want to return "Full.TypeName.Property"
//					if (prop.Modifiers.HasFlag (ICSharpCode.NRefactory.CSharp.Modifiers.Static))
//						return mr.Member.DeclaringType.FullName + "." + name;
//
//					// otherwise we want to return "this.Property" so that it won't conflict with anything else in the local scope
//					return "this." + name;
//				}
//
//				if (node is ICSharpCode.NRefactory.CSharp.FieldDeclaration) {
//					var field = (ICSharpCode.NRefactory.CSharp.FieldDeclaration) node;
//					var name = GetIdentifierName (editor, field.NameToken, out startOffset);
//
//					// if the field is static, then we want to return "Full.TypeName.Field"
//					if (field.Modifiers.HasFlag (ICSharpCode.NRefactory.CSharp.Modifiers.Static))
//						return mr.Member.DeclaringType.FullName + "." + name;
//
//					// otherwise we want to return "this.Field" so that it won't conflict with anything else in the local scope
//					return "this." + name;
//				}
//
//				if (node is ICSharpCode.NRefactory.CSharp.VariableInitializer) {
//					// user is hovering over a field declaration that includes initialization
//					var variable = (ICSharpCode.NRefactory.CSharp.VariableInitializer) node;
//					var name = GetIdentifierName (editor, variable.NameToken, out startOffset);
//
//					// walk up the AST to find the FieldDeclaration so that we can determine if it is static or not
//					var field = variable.GetParent<ICSharpCode.NRefactory.CSharp.FieldDeclaration> ();
//
//					// if the field is static, then we want to return "Full.TypeName.Field"
//					if (field.Modifiers.HasFlag (ICSharpCode.NRefactory.CSharp.Modifiers.Static))
//						return mr.Member.DeclaringType.FullName + "." + name;
//
//					// otherwise we want to return "this.Field" so that it won't conflict with anything else in the local scope
//					return "this." + name;
//				}
//
//				if (node is ICSharpCode.NRefactory.CSharp.NamedExpression) {
//					// user is hovering over 'Property' in an expression like: var fubar = new Fubar () { Property = baz };
//					var variable = node.GetParent<ICSharpCode.NRefactory.CSharp.VariableInitializer> ();
//					if (variable != null) {
//						var variableName = GetIdentifierName (editor, variable.NameToken, out startOffset);
//						var name = GetIdentifierName (editor, ((ICSharpCode.NRefactory.CSharp.NamedExpression) node).NameToken, out startOffset);
//
//						return variableName + "." + name;
//					}
//				}
//			} else if (result is TypeResolveResult) {
//				return ((TypeResolveResult) result).Type.FullName;
//			}
//
//			return editor.GetTextBetween (node.StartLocation, node.EndLocation);
//		}

		string IDebuggerExpressionResolver.ResolveExpression (IReadonlyTextDocument editor, MonoDevelop.Ide.Gui.Document doc, int offset, out int startOffset)
		{
			// TODO: Roslyn port!
			startOffset = -1;
			return null;
			/*
			ResolveResult result;
			AstNode node;

			var loc = editor.OffsetToLocation (offset);
			if (!TryResolveAt (doc, loc, out result, out node)) {
				startOffset = -1;
				return null;
			}

			return ResolveExpression (editor, result, node, out startOffset);*/
		}

		#endregion
		
		#region TypeSystemSegmentTree

		TypeSystemSegmentTree validTypeSystemSegmentTree;

		internal class TypeSystemTreeSegment : TreeSegment
		{
			public SyntaxNode Entity {
				get;
				private set;
			}
			
			public TypeSystemTreeSegment (int offset, int length, SyntaxNode entity) : base (offset, length)
			{
				this.Entity = entity;
			}
		}

		internal TypeSystemTreeSegment GetMemberSegmentAt (int offset)
		{
			TypeSystemTreeSegment result = null;
			if (result == null && validTypeSystemSegmentTree != null)
				result = validTypeSystemSegmentTree.GetMemberSegmentAt (offset);
			return result;
		}
		
		internal class TypeSystemSegmentTree : SegmentTree<TypeSystemTreeSegment>
		{
			public SyntaxNode GetMemberAt (int offset)
			{
				// Members don't overlap
				var seg = GetSegmentsAt (offset).FirstOrDefault ();
				if (seg == null)
					return null;
				return seg.Entity;
			}
			
			public TypeSystemTreeSegment GetMemberSegmentAt (int offset)
			{
				return GetSegmentsAt (offset).LastOrDefault ();
			}

			
			
			internal static TypeSystemSegmentTree Create (MonoDevelop.Ide.Editor.TextEditor editor, DocumentContext ctx, SemanticModel semanticModel)
			{
				var visitor = new TreeVisitor ();
				visitor.Visit (semanticModel.SyntaxTree.GetRoot ()); 
				return visitor.Result;
			}

			class TreeVisitor : CSharpSyntaxWalker
			{
				public TypeSystemSegmentTree Result = new TypeSystemSegmentTree ();

				public override void VisitClassDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax node)
				{
					Result.Add (new TypeSystemTreeSegment (node.SpanStart, node.Span.Length, node));
					base.VisitClassDeclaration (node);
				}
				public override void VisitStructDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax node)
				{
					Result.Add (new TypeSystemTreeSegment (node.SpanStart, node.Span.Length, node));
					base.VisitStructDeclaration (node);
				}

				public override void VisitInterfaceDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax node)
				{
					Result.Add (new TypeSystemTreeSegment (node.SpanStart, node.Span.Length, node));
					base.VisitInterfaceDeclaration (node);
				}

				public override void VisitEnumDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.EnumDeclarationSyntax node)
				{
					Result.Add (new TypeSystemTreeSegment (node.SpanStart, node.Span.Length, node));
				}

				public override void VisitPropertyDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax node)
				{
					Result.Add (new TypeSystemTreeSegment (node.SpanStart, node.Span.Length, node));
				}

				public override void VisitMethodDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax node)
				{
					Result.Add (new TypeSystemTreeSegment (node.SpanStart, node.Span.Length, node));
				}

				public override void VisitConstructorDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax node)
				{
					Result.Add (new TypeSystemTreeSegment (node.SpanStart, node.Span.Length, node));
				}

				public override void VisitDestructorDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.DestructorDeclarationSyntax node)
				{
					Result.Add (new TypeSystemTreeSegment (node.SpanStart, node.Span.Length, node));
				}

				public override void VisitIndexerDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.IndexerDeclarationSyntax node)
				{
					Result.Add (new TypeSystemTreeSegment (node.SpanStart, node.Span.Length, node));
				}

				public override void VisitDelegateDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.DelegateDeclarationSyntax node)
				{
					Result.Add (new TypeSystemTreeSegment (node.SpanStart, node.Span.Length, node));
				}

				public override void VisitOperatorDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.OperatorDeclarationSyntax node)
				{
					Result.Add (new TypeSystemTreeSegment (node.SpanStart, node.Span.Length, node));
				}

				public override void VisitEventDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.EventDeclarationSyntax node)
				{
					Result.Add (new TypeSystemTreeSegment (node.SpanStart, node.Span.Length, node));
				}

				public override void VisitBlock (Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax node)
				{
					// nothing
				}
			}
			
		}
		
		public SyntaxNode GetMemberAt (int offset)
		{
			SyntaxNode member = null;
			if (member == null && validTypeSystemSegmentTree != null)
				member = validTypeSystemSegmentTree.GetMemberAt (offset);

			return member;
		}
		#endregion
	}
}

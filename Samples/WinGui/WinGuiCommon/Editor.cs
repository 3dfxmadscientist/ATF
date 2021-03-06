//Copyright � 2014 Sony Computer Entertainment America LLC. See License.txt.

using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Windows.Forms;

using Sce.Atf;
using Sce.Atf.Adaptation;
using Sce.Atf.Applications;
using Sce.Atf.Dom;

namespace WinGuiCommon
{
    /// <summary>
    /// Editor class component that creates and saves application documents.
    /// This document client handles file operations, such as opening and closing documents,
    /// including persisting data. It registers a control (ListView) with a IControlHostService
    /// so that the control appears in the Windows docking framework.</summary>
    [Export(typeof(IDocumentClient))]
    [Export(typeof(Editor))]
    [Export(typeof(IInitializable))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class Editor : IDocumentClient, IControlHostClient, IInitializable
    {
        /// <summary>
        /// Constructor</summary>
        /// <param name="controlHostService">Control host service</param>
        /// <param name="documentService">Document service</param>
        /// <param name="contextRegistry">Context registry</param>
        /// <param name="documentRegistry">Document registry</param>
        /// <param name="schemaLoader">Schema loader</param>
        [ImportingConstructor]
        public Editor(
            IControlHostService controlHostService,
            IDocumentService documentService,
            IContextRegistry contextRegistry,
            IDocumentRegistry documentRegistry,
            SchemaLoader schemaLoader)
        {
            m_controlHostService = controlHostService;
            m_documentService = documentService;
            m_contextRegistry = contextRegistry;
            m_documentRegistry = documentRegistry;
            m_schemaLoader = schemaLoader;
        }

        private IControlHostService m_controlHostService;
        private IDocumentService m_documentService;
        private IContextRegistry m_contextRegistry;
        private IDocumentRegistry m_documentRegistry;
        private SchemaLoader m_schemaLoader;

        [Import(AllowDefault = true)]
        private ScriptingService m_scriptingService = null;

        #region IInitializable Members

        /// <summary>
        /// Finishes initializing component by setting up scripting service</summary>
        void IInitializable.Initialize()
        {
            if (m_scriptingService != null)
            {
                // load this assembly into script domain.
                m_scriptingService.LoadAssembly(GetType().Assembly);
                m_scriptingService.ImportAllTypes("WinGuiCommon");

                m_scriptingService.SetVariable("editor", this);

                m_contextRegistry.ActiveContextChanged += delegate
                {
                    EditingContext editingContext = m_contextRegistry.GetActiveContext<EditingContext>();
                    IHistoryContext hist = m_contextRegistry.GetActiveContext<IHistoryContext>();
                    m_scriptingService.SetVariable("editingContext", editingContext);
                    m_scriptingService.SetVariable("hist", hist);
                };
            }
        }

        #endregion

        #region IDocumentClient Members

        /// <summary>
        /// Gets information about the document client, such as the file type and file
        /// extensions it supports, whether or not it allows multiple documents to be open,
        /// etc.</summary>
        public DocumentClientInfo Info
        {
            get { return DocumentClientInfo; }
        }

        /// <summary>
        /// Document editor information for editor</summary>
        public static DocumentClientInfo DocumentClientInfo = new DocumentClientInfo(
            Localizer.Localize("Gui App Data"),
            new string[] { ".gad" },
            Sce.Atf.Resources.DocumentImage,
            Sce.Atf.Resources.FolderImage,
            true);

        /// <summary>
        /// Returns whether the client can open or create a document at the given URI</summary>
        /// <param name="uri">Document URI</param>
        /// <returns>True iff the client can open or create a document at the given URI</returns>
        public bool CanOpen(Uri uri)
        {
            return DocumentClientInfo.IsCompatibleUri(uri);
        }

        /// <summary>
        /// Opens or creates a document at the given URI</summary>
        /// <param name="uri">Document URI</param>
        /// <returns>Document, or null if the document couldn't be opened or created</returns>
        public IDocument Open(Uri uri)
        {
            DomNode node = null;
            string filePath = uri.LocalPath;
            string fileName = Path.GetFileName(filePath);

            if (File.Exists(filePath))
            {
                // read existing document using standard XML reader
                using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    DomXmlReader reader = new DomXmlReader(m_schemaLoader);
                    node = reader.Read(stream, uri);
                }
            }
            else
            {
                // create new document by creating a Dom node of the root type defined by the schema
                node = new DomNode(Schema.winGuiCommonDataType.Type, Schema.winGuiCommonDataRootElement);
            }

            WinGuiCommonDataDocument document = null;
            if (node != null)
            {
                // Initialize Dom extensions now that the data is complete
                node.InitializeExtensions();

                WinGuiCommonDataContext context = node.As<WinGuiCommonDataContext>();

                ControlInfo controlInfo = new ControlInfo(fileName, filePath, StandardControlGroup.Center);
                context.ControlInfo = controlInfo;

                // set document URI
                document = node.As<WinGuiCommonDataDocument>();
                document.Uri = uri;

                context.ListView.Tag = document;

                // show the ListView control
                m_controlHostService.RegisterControl(context.ListView, controlInfo, this);
            }

            return document;
        }

        /// <summary>
        /// Makes the document visible to the user</summary>
        /// <param name="document">Document to show</param>
        public void Show(IDocument document)
        {
            WinGuiCommonDataContext context = Adapters.As<WinGuiCommonDataContext>(document);
            m_controlHostService.Show(context.ListView);
        }

        /// <summary>
        /// Saves the document at the given URI</summary>
        /// <param name="document">Document to save</param>
        /// <param name="uri">New document URI</param>
        public void Save(IDocument document, Uri uri)
        {
            string filePath = uri.LocalPath;
            FileMode fileMode = File.Exists(filePath) ? FileMode.Truncate : FileMode.OpenOrCreate;
            using (FileStream stream = new FileStream(filePath, fileMode))
            {
                DomXmlWriter writer = new DomXmlWriter(m_schemaLoader.TypeCollection);
                WinGuiCommonDataDocument eventSequenceDocument = (WinGuiCommonDataDocument)document;
                writer.Write(eventSequenceDocument.DomNode, stream, uri);
            }
        }

        /// <summary>
        /// Closes the document and removes any views of it from the UI</summary>
        /// <param name="document">Document to close</param>
        public void Close(IDocument document)
        {
            WinGuiCommonDataContext context = Adapters.As<WinGuiCommonDataContext>(document);
            m_controlHostService.UnregisterControl(context.ListView);
            context.ControlInfo = null;

            // close all active EditingContexts in the document
            foreach (DomNode node in context.DomNode.Subtree)
                foreach (EditingContext editingContext in node.AsAll<EditingContext>())
                    m_contextRegistry.RemoveContext(editingContext);

            // close the document
            m_documentRegistry.Remove(document);
        }

        #endregion

        #region IControlHostClient Members

        /// <summary>
        /// Notifies the client that its Control has been activated. Activation occurs when
        /// the Control gets focus, or a parent "host" Control gets focus.</summary>
        /// <param name="control">Client Control that was activated</param>
        /// <remarks>This method is only called by IControlHostService if the Control was previously
        /// registered for this IControlHostClient.</remarks>
        void IControlHostClient.Activate(Control control)
        {
            WinGuiCommonDataDocument document = control.Tag as WinGuiCommonDataDocument;
            if (document != null)
            {
                m_documentRegistry.ActiveDocument = document;

                WinGuiCommonDataContext context = document.As<WinGuiCommonDataContext>();
                m_contextRegistry.ActiveContext = context;
            }
        }

        /// <summary>
        /// Notifies the client that its Control has been deactivated. Deactivation occurs when
        /// another Control or "host" Control gets focus.</summary>
        /// <param name="control">Client Control that was deactivated</param>
        /// <remarks>This method is only called by IControlHostService if the Control was previously
        /// registered for this IControlHostClient.</remarks>
        void IControlHostClient.Deactivate(Control control)
        {
        }

        /// <summary>
        /// Requests permission to close the client's Control.
        /// Allows user to save document before it closes.</summary>
        /// <param name="control">Client Control to be closed</param>
        /// <returns>True if the Control can close, or false to cancel</returns>
        /// <remarks>
        /// 1. This method is only called by IControlHostService if the Control was previously
        /// registered for this IControlHostClient.
        /// 2. If true is returned, the IControlHostService calls its own
        /// UnregisterControl. The IControlHostClient has to call RegisterControl again
        /// if it wants to re-register this Control.</remarks>
        bool IControlHostClient.Close(Control control)
        {
            bool closed = true;

            WinGuiCommonDataDocument document = control.Tag as WinGuiCommonDataDocument;
            if (document != null)
            {
                closed = m_documentService.Close(document);
                if (closed)
                    m_contextRegistry.RemoveContext(document);
            }

            return closed;
        }

        #endregion
    }
}

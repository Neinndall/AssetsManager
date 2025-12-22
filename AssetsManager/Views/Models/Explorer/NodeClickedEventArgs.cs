using System;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Views.Models.Explorer
{
    public class NodeClickedEventArgs : EventArgs
    {
        public FileSystemNodeModel Node { get; }

        public NodeClickedEventArgs(FileSystemNodeModel node)
        {
            Node = node;
        }
    }
}

//using System.Linq;
//using UnityEngine;
//using UnityEngine.UIElements;
//using FraxinusEngine.Base;

//namespace FraxinusEditor.Base
//{
//    [NodeCustomEditor(typeof(RelayNode))]
//    public class RelayNodeView : BaseNodeView
//    {
//        RelayNode _relay => nodeTarget as RelayNode;
//        VisualElement _input => this.Q("input");
//        VisualElement _output => this.Q("output");

//        public override void Enable()
//        {
//            // Remove useless elements
//            this.Q("title").RemoveFromHierarchy();
//            this.Q("divider").RemoveFromHierarchy();

//            _relay.onPortsUpdated += _ => UpdateSize();
//        }

//        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
//        {
//            // TODO: check if there is a relay node in the inputs that have pack option and toggle visibility of these options:
//            evt.menu.AppendAction("Pack Input", TogglePackInput, PackInputStatus);
//            evt.menu.AppendAction("Unpack Output", ToggleUnpackOutput, UnpackOutputStatus);
//            base.BuildContextualMenu(evt);
//        }

//        void TogglePackInput(DropdownMenuAction action)
//        {
//            _relay.packInput = !_relay.packInput;

//            ForceUpdatePorts();
//            UpdateSize();
//            MarkDirtyRepaint();
//        }

//        void ToggleUnpackOutput(DropdownMenuAction action)
//        {
//            _relay.unpackOutput = !_relay.unpackOutput;

//            ForceUpdatePorts();
//            UpdateSize();
//            MarkDirtyRepaint();
//        }

//        DropdownMenuAction.Status PackInputStatus(DropdownMenuAction action)
//        {
//            if (_relay.GetNonRelayEdges().Count != 1)
//                return DropdownMenuAction.Status.Disabled;

//            if (_relay.packInput)
//                return DropdownMenuAction.Status.Checked;
//            else
//                return DropdownMenuAction.Status.Normal;
//        }

//        DropdownMenuAction.Status UnpackOutputStatus(DropdownMenuAction action)
//        {
//            if (_relay.GetNonRelayEdges().Count == 0)
//                return DropdownMenuAction.Status.Disabled;

//            if (_relay.unpackOutput)
//                return DropdownMenuAction.Status.Checked;
//            else
//                return DropdownMenuAction.Status.Normal;
//        }

//        public override void SetPosition(Rect newPos)
//        {
//            base.SetPosition(new Rect(newPos.position, new Vector2(200, 200)));
//            UpdateSize();
//        }

//        void UpdateSize()
//        {
//            if (_relay.unpackOutput)
//            {
//                int inputEdgeCount = _relay.GetNonRelayEdges().Count + 1;
//                style.height = Mathf.Max(30, 24 * inputEdgeCount + 5);
//                style.width = -1;
//                if (_input != null)
//                    _input.style.height = -1;
//                if (_output != null)
//                    _output.style.height = -1;
//                RemoveFromClassList("hideLabels");
//            }
//            else
//            {
//                style.height = 20;
//                style.width = 50;
//                if (_input != null)
//                    _input.style.height = 16;
//                if (_output != null)
//                    _output.style.height = 16;
//                AddToClassList("hideLabels");
//            }
//        }

//        public override void OnRemoved()
//        {
//            schedule.Execute(() => 
//            {
//                if (!_relay.unpackOutput)
//                {
//                    var inputEdges = inputPortViews[0].GetEdges();
//                    var outputEdges = outputPortViews[0].GetEdges();

//                    if (inputEdges.Count == 0 || outputEdges.Count == 0)
//                        return;

//                    var inputEdge = inputEdges.First();
//                    foreach (var outputEdge in outputEdges.ToList())
//                    {
//                        var input = outputEdge.input as PortView;
//                        var output = inputEdge.output as PortView;
//                        owner.Connect(input, output);
//                    }
//                }
//            }).ExecuteLater(1);
//        }
//    }
//}
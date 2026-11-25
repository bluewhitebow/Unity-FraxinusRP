using UnityEngine.UIElements;
using FraxinusEngine.Base;

namespace FraxinusEditor.Base
{
    public class ProcessorView : PinnedElementView
    {
        BaseGraphProcessor _processor;

        public ProcessorView()
        {
            title = "Process panel";
        }

        protected override void Initialize(BaseGraphView graphView)
        {
            _processor = new ProcessGraphProcessor(graphView.graph);
            Button b = new Button(OnPlay) 
            {
                name = "ActionButton",
                text = "Play !"
            };

            _content.Add(b);
        }

        protected override void Destroy()
        {
            _processor?.Dispose();
            _processor = null;
            base.Destroy();
        }

        void OnPlay()
        {
            _processor.Run();
        }
    }
}

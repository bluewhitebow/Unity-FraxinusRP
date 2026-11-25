using UnityEngine;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(ChannelMixer))]
    sealed class ChannelMixerEditor : VolumeComponentEditor
    {
        SerializedDataParameter _redOutRedIn;
        SerializedDataParameter _redOutGreenIn;
        SerializedDataParameter _redOutBlueIn;
        SerializedDataParameter _greenOutRedIn;
        SerializedDataParameter _greenOutGreenIn;
        SerializedDataParameter _greenOutBlueIn;
        SerializedDataParameter _blueOutRedIn;
        SerializedDataParameter _blueOutGreenIn;
        SerializedDataParameter _blueOutBlueIn;

        SavedInt _selectedChannel;

        public override void OnEnable()
        {
            var o = new PropertyFetcher<ChannelMixer>(serializedObject);

            _redOutRedIn = Unpack(o.Find(x => x.redOutRedIn));
            _redOutGreenIn = Unpack(o.Find(x => x.redOutGreenIn));
            _redOutBlueIn = Unpack(o.Find(x => x.redOutBlueIn));
            _greenOutRedIn = Unpack(o.Find(x => x.greenOutRedIn));
            _greenOutGreenIn = Unpack(o.Find(x => x.greenOutGreenIn));
            _greenOutBlueIn = Unpack(o.Find(x => x.greenOutBlueIn));
            _blueOutRedIn = Unpack(o.Find(x => x.blueOutRedIn));
            _blueOutGreenIn = Unpack(o.Find(x => x.blueOutGreenIn));
            _blueOutBlueIn = Unpack(o.Find(x => x.blueOutBlueIn));

            _selectedChannel = new SavedInt($"{target.GetType()}.SelectedChannel", 0);
        }

        public override void OnInspectorGUI()
        {
            int currentChannel = _selectedChannel.value;

            EditorGUI.BeginChangeCheck();
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Toggle(currentChannel == 0, EditorGUIUtility.TrTextContent("Red", "Red output channel."), EditorStyles.miniButtonLeft)) currentChannel = 0;
                    if (GUILayout.Toggle(currentChannel == 1, EditorGUIUtility.TrTextContent("Green", "Green output channel."), EditorStyles.miniButtonMid)) currentChannel = 1;
                    if (GUILayout.Toggle(currentChannel == 2, EditorGUIUtility.TrTextContent("Blue", "Blue output channel."), EditorStyles.miniButtonRight)) currentChannel = 2;
                }
            }
            if (EditorGUI.EndChangeCheck())
                GUI.FocusControl(null);

            _selectedChannel.value = currentChannel;

            if (currentChannel == 0)
            {
                PropertyField(_redOutRedIn, EditorGUIUtility.TrTextContent("Red"));
                PropertyField(_redOutGreenIn, EditorGUIUtility.TrTextContent("Green"));
                PropertyField(_redOutBlueIn, EditorGUIUtility.TrTextContent("Blue"));
            }
            else if (currentChannel == 1)
            {
                PropertyField(_greenOutRedIn, EditorGUIUtility.TrTextContent("Red"));
                PropertyField(_greenOutGreenIn, EditorGUIUtility.TrTextContent("Green"));
                PropertyField(_greenOutBlueIn, EditorGUIUtility.TrTextContent("Blue"));
            }
            else
            {
                PropertyField(_blueOutRedIn, EditorGUIUtility.TrTextContent("Red"));
                PropertyField(_blueOutGreenIn, EditorGUIUtility.TrTextContent("Green"));
                PropertyField(_blueOutBlueIn, EditorGUIUtility.TrTextContent("Blue"));
            }
        }
    }
}

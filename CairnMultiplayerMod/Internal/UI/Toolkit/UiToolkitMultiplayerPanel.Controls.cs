using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace CairnMultiplayerMod.Internal.UI.Toolkit;

internal sealed partial class UiToolkitMultiplayerPanel
{
    private sealed class StepperControl
    {
        private readonly int _min;
        private readonly int _max;
        private readonly Label _valueLabel;
        private readonly Button _minus;
        private readonly Button _plus;

        public VisualElement Root { get; }
        public int Value { get; private set; }

        public StepperControl(int value, int min, int max)
        {
            _min = min;
            _max = max;
            Value = Mathf.Clamp(value, min, max);

            Root = CairnUi.Row(0);
            Root.style.height = 38;
            Root.style.marginBottom = 0;

            _minus = CairnUi.Button("-", () => Change(-1));
            _minus.style.width = 38;
            Root.Add(_minus);

            _valueLabel = CairnUi.Label(Value.ToString(), 16, CairnUi.Accent, FontStyle.Bold);
            _valueLabel.style.flexGrow = 1;
            _valueLabel.style.backgroundColor = CairnUi.Surface;
            _valueLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            Root.Add(_valueLabel);

            _plus = CairnUi.Button("+", () => Change(1));
            _plus.style.width = 38;
            Root.Add(_plus);
        }

        public void SetEnabled(bool enabled)
        {
            _minus.SetEnabled(enabled);
            _plus.SetEnabled(enabled);
        }

        private void Change(int delta)
        {
            Value = Mathf.Clamp(Value + delta, _min, _max);
            _valueLabel.text = Value.ToString();
        }
    }

    private sealed class SegmentControl
    {
        private readonly List<Button> _buttons = new();
        private readonly string[] _labels;

        public VisualElement Root { get; }
        public int SelectedIndex { get; private set; }
        public event Action<int> Changed;

        public SegmentControl(string[] labels, int initialIndex)
        {
            _labels = labels;
            SelectedIndex = Mathf.Clamp(initialIndex, 0, labels.Length - 1);

            Root = CairnUi.Row(0);
            Root.style.height = 38;
            Root.style.marginBottom = 0;

            for (var i = 0; i < labels.Length; i++)
            {
                var captured = i;
                var button = CairnUi.Button(labels[i], () => Select(captured));
                button.style.flexGrow = 1;
                if (i > 0) button.style.marginLeft = 4;
                Root.Add(button);
                _buttons.Add(button);
            }

            Refresh();
        }

        public void SetEnabled(bool enabled)
        {
            foreach (var button in _buttons)
                button.SetEnabled(enabled);
        }

        private void Select(int index)
        {
            if (index == SelectedIndex) return;
            SelectedIndex = index;
            Refresh();
            Changed?.Invoke(index);
        }

        private void Refresh()
        {
            for (var i = 0; i < _buttons.Count; i++)
            {
                var selected = i == SelectedIndex;
                _buttons[i].text = _labels[i];
                _buttons[i].style.backgroundColor = selected ? CairnUi.AccentSoft : CairnUi.Surface;
                _buttons[i].style.color = selected ? CairnUi.Accent : CairnUi.TextMuted;
                CairnUi.SetBorderColor(_buttons[i], selected ? CairnUi.Accent : CairnUi.BorderSubtle);
            }
        }
    }
}

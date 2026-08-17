using System.Windows;
using System.Windows.Controls;
using LenSa.Core.Chat.Models;

namespace LenSa.Core.Chat.Views
{
    /// <summary>
    /// Селектор шаблонов сообщений — по ChatMessage.Role выбирает DataTemplate.
    /// Шаблоны объявляются в ChatView.xaml как ресурсы с x:Key = имя роли + "Template".
    /// </summary>
    public class MessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? UserTemplate { get; set; }
        public DataTemplate? AssistantTemplate { get; set; }
        public DataTemplate? ToolUseTemplate { get; set; }
        public DataTemplate? ToolResultTemplate { get; set; }
        public DataTemplate? DebugTemplate { get; set; }
        public DataTemplate? CheckpointTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is not ChatMessage msg) return base.SelectTemplate(item, container);

            return msg.Role switch
            {
                MessageRoles.User => UserTemplate,
                MessageRoles.Assistant => AssistantTemplate,
                MessageRoles.ToolUse => ToolUseTemplate,
                MessageRoles.ToolResult => ToolResultTemplate,
                MessageRoles.Debug => DebugTemplate,
                MessageRoles.Checkpoint => CheckpointTemplate,
                _ => AssistantTemplate
            };
        }
    }
}

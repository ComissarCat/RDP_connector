namespace RDP_connector
{
    public class ManagedComputer
    {
        public string Host { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        // Красивое отображение в меню Spectre.Console
        public override string ToString() => $"{Host} ({Description})";
    }
}
using p5rpc.ultrawide.Template.Configuration;
using System.ComponentModel;

namespace p5rpc.ultrawide.Configuration
{
    public class Config : Configurable<Config>
    {
        /*
            User Properties:
                - Please put all of your configurable properties here.

            By default, configuration saves as "Config.json" in mod user config folder.    
            Need more config files/classes? See Configuration.cs

            Available Attributes:
            - Category
            - DisplayName
            - Description
            - DefaultValue

            // Technically Supported but not Useful
            - Browsable
            - Localizable

            The `DefaultValue` attribute is used as part of the `Reset` button in Reloaded-Launcher.
        */

        //[DisplayName("String")]
        //[Description("This is a string.")]
        //[DefaultValue("Default Name")]
        //public string String { get; set; } = "Default Name";

        //[DisplayName("Int")]
        //[Description("This is an int.")]
        //[DefaultValue(42)]
        //public int Integer { get; set; } = 42;

        //[DisplayName("Bool")]
        //[Description("This is a bool.")]
        //[DefaultValue(true)]
        //public bool Boolean { get; set; } = true;

        //[DisplayName("Float")]
        //[Description("This is a floating point number.")]
        //[DefaultValue(6.987654F)]
        //public float Float { get; set; } = 6.987654F;

        //[DisplayName("Enum")]
        //[Description("This is an enumerable.")]
        //[DefaultValue(SampleEnum.ILoveIt)]
        //public SampleEnum Reloaded { get; set; } = SampleEnum.ILoveIt;

        //public enum SampleEnum
        //{
        //    NoOpinion,
        //    Sucks,
        //    IsMediocre,
        //    IsOk,
        //    IsCool,
        //    ILoveIt
        //}

        [DisplayName("Fix Mouse Position")]
        [Description("Maps the mouse onto the centred 16:9 UI so hovering and clicking line up. Makes horizontal mouse-look slightly faster.")]
        [DefaultValue(true)]
        public bool FixMouse { get; set; } = true;

        [DisplayName("Enable Resolution Override")]
        [Description("Forces a resolution.")]
        [DefaultValue(false)]
        public bool Override { get; set; } = false;

        [DisplayName("Width Override")]
        [Description("Custom Resolution Width")]
        [DefaultValue(0)]
        public int Width { get; set; } = 0;

        [DisplayName("Height Override")]
        [Description("Custom Resolution Height")]
        [DefaultValue(0)]
        public int Height { get; set; } = 0;

        [Category("Debug")]
        [DisplayName("Debug Logging")]
        [Description("Logs screen and UI state on resolution changes and when F9 is pressed.")]
        [DefaultValue(false)]
        public bool DebugLogging { get; set; } = false;

        [Category("Debug")]
        [DisplayName("Load RenderDoc")]
        [Description("Loads RenderDoc into the game so frames can be captured with F12. Requires a restart.")]
        [DefaultValue(false)]
        public bool LoadRenderDoc { get; set; } = false;

        [Category("Debug")]
        [DisplayName("RenderDoc DLL Path")]
        [DefaultValue(@"C:\Program Files\RenderDoc\renderdoc.dll")]
        public string RenderDocPath { get; set; } = @"C:\Program Files\RenderDoc\renderdoc.dll";

        [Category("Debug")]
        [DisplayName("RenderDoc Capture Path")]
        [Description("Capture file path template (environment variables allowed). Empty uses RenderDoc's default.")]
        [DefaultValue("")]
        public string RenderDocCaptureTemplate { get; set; } = "";
    }

    /// <summary>
    /// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
    /// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
    /// </summary>
    public class ConfiguratorMixin : ConfiguratorMixinBase
    {
        // 
    }
}
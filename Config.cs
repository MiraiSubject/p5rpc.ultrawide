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

        [Category("Mouse")]
        [DisplayName("Match Mouse to Centred UI")]
        [Description("On: the mouse highlights and clicks exactly what the cursor is pointing at. Off: the game " +
            "treats the mouse as if the menus stretched across the whole screen, so the highlighted item is off to " +
            "the side of the cursor, more so the further the cursor is from the centre. Side effect when on: turning " +
            "the camera with the mouse is faster left and right, about 1.3x on a 21:9 screen and 2x on 32:9. " +
            "Takes effect immediately.")]
        [DefaultValue(true)]
        public bool FixMouse { get; set; } = true;

        [Category("Resolution")]
        [DisplayName("Override Resolution")]
        [Description("On: the game uses the Width and Height below instead of its own resolution setting. Off: the " +
            "game's own resolution setting is used. Applies the next time the game sets its resolution; restart the " +
            "game to be sure.")]
        [DefaultValue(false)]
        public bool Override { get; set; } = false;

        [Category("Resolution")]
        [DisplayName("Width")]
        [Description("Horizontal resolution in pixels. Only used when Override Resolution is on. Set to 0 to keep " +
            "the game's own width.")]
        [DefaultValue(0)]
        public int Width { get; set; } = 0;

        [Category("Resolution")]
        [DisplayName("Height")]
        [Description("Vertical resolution in pixels. Only used when Override Resolution is on. Set to 0 to keep " +
            "the game's own height.")]
        [DefaultValue(0)]
        public int Height { get; set; } = 0;

        [Category("Debug")]
        [DisplayName("Debug Logging")]
        [Description("For troubleshooting only; leave off for normal play. Writes screen size and UI scaling details " +
            "to the Reloaded-II log whenever the resolution changes, and enables two keys in game: F9 logs the " +
            "current state, F10 logs how the next 400 UI draws are adjusted. Requires a game restart.")]
        [DefaultValue(false)]
        public bool DebugLogging { get; set; } = false;

        [Category("Debug")]
        [DisplayName("Load RenderDoc")]
        [Description("For troubleshooting graphics only; leave off for normal play. Loads the RenderDoc graphics " +
            "debugger into the game (RenderDoc must be installed). Press F12 to capture a frame; this switches the " +
            "game's button prompts to keyboard for that frame. Hold L3+R3 on a controller to capture with controller " +
            "prompts instead. Requires a game restart.")]
        [DefaultValue(false)]
        public bool LoadRenderDoc { get; set; } = false;

        [Category("Debug")]
        [DisplayName("RenderDoc DLL Path")]
        [Description("Where renderdoc.dll is on your PC. The default matches a standard RenderDoc install. Only " +
            "used when Load RenderDoc is on.")]
        [DefaultValue(@"C:\Program Files\RenderDoc\renderdoc.dll")]
        public string RenderDocPath { get; set; } = @"C:\Program Files\RenderDoc\renderdoc.dll";

        [Category("Debug")]
        [DisplayName("RenderDoc Capture Path")]
        [Description(@"Where captures are saved: a folder plus the start of the file name. For example, " +
            @"%USERPROFILE%\Captures\p5r saves files like p5r_frame123.rdc in %USERPROFILE%\Captures. " +
            "Environment variables such as %USERPROFILE% work. Leave empty to use RenderDoc's default folder. " +
            "Only used when Load RenderDoc is on.")]
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
using System;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace CodexUsageBar {
// Only menu geometry is queried. No text from conversations is read or stored.
static class TitleAnchor {
    static IntPtr window;
    static double logicalOffset=-1;
    static bool busy;
    static DateTime next=DateTime.MinValue;
    public static bool Measured { get { return logicalOffset>=0; } }
    public static int Left(IntPtr handle,int windowLeft,float dpi) {
        if(handle!=window) { window=handle;logicalOffset=-1;next=DateTime.MinValue; }
        if(!busy&&DateTime.UtcNow>=next) Probe(handle,dpi);
        return windowLeft+(int)Math.Ceiling((logicalOffset>=0?logicalOffset:64)*dpi);
    }
    static async void Probe(IntPtr handle,float dpi) {
        busy=true;next=DateTime.UtcNow.AddSeconds(4);
        double offset=await Task.Run(delegate {
            try {
                Native.Rect bounds;if(!Native.GetWindowRect(handle,out bounds))return -1d;
                var root=AutomationElement.FromHandle(handle);
                var items=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem));
                foreach(AutomationElement item in items) {
                    string name=item.Current.Name.Replace("&","").Trim();var rect=item.Current.BoundingRectangle;
                    if((name.StartsWith("帮助",StringComparison.Ordinal)||name.Equals("Help",StringComparison.OrdinalIgnoreCase))&&rect.Top>=bounds.Top&&rect.Top<bounds.Top+80*dpi&&rect.Right>bounds.Left)
                        return (rect.Right-bounds.Left)/dpi;
                }
            } catch(ElementNotAvailableException) {} catch(InvalidOperationException) {} catch(System.Runtime.InteropServices.COMException) {}
            return -1d;
        });
        if(window==handle&&offset>=0)logicalOffset=offset;
        busy=false;
    }
}
}

using System;
using System.Collections.Generic;
using System.Text;
using ModelFramework;

[Description("Base class from which other functions inherit")]
abstract public class Function
{
    abstract public double Value { get; }
    virtual public string ValueString { get { return Value.ToString(); } }

    [Link]
    public Component My = null;

    public string Name { get { return My.Name; } }
}

using System.Text.RegularExpressions;
namespace SelfService.Portal;

public record ResourceIdentity(string Name, string FullName, string Type, string ParentId)
{
    public static ResourceIdentity Parse(string id)
    {
        if (id.Length > 2048 || Regex.IsMatch(id, @"[\s?#%\\]|/\.{1,2}(/|$)")) throw new PortalException("Invalid resource identity.",409);
        var p=id.Split('/');
        if(p.Length<5 || p[0]!="" || !p[1].Equals("subscriptions",StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(p[2],out _)
            || !p[3].Equals("resourceGroups",StringComparison.OrdinalIgnoreCase) || p[4].Length==0) throw new PortalException("Resource-group qualified identity required.",409);
        var type="Microsoft.Resources/resourceGroups";var names=new List<string>{p[4]};var parent="/"+string.Join('/',p[1..3]);
        for(var i=5;i<p.Length;){
            if(!p[i].Equals("providers",StringComparison.OrdinalIgnoreCase) || i+3>=p.Length)throw new PortalException("Invalid provider identity.",409);
            type=p[i+1];names.Clear();i+=2;
            while(i<p.Length && !p[i].Equals("providers",StringComparison.OrdinalIgnoreCase)){
                if(i+1>=p.Length || p[i].Length==0 || p[i+1].Length==0)throw new PortalException("Invalid child identity.",409);
                parent="/"+string.Join('/',p[1..(names.Count>0?i:i-2)]);type+="/"+p[i];names.Add(p[i+1]);i+=2;
            }
        }
        return new(names[^1],string.Join('/',names),type,parent);
    }
}

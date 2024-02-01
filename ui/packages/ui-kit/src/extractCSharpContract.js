
const fs = require('fs');


const selection = JSON.parse(fs.readFileSync('./fonts/sm-icons/selection.json', { encoding: 'utf8' }));

const icons = selection.icons
                .map(i => i.properties.name)
                .map(n => ({
                    cSharpName: n.replace(/\w+/g ,w => `${w[0].toUpperCase()}${w.slice(1).toLowerCase()}`).replace(/\W+/g, ''),
                    icon: `sm-${n}`
                }))
                .map(x => `public Icon ${x.cSharpName} = new (Provider, "${x.icon}");`);

const ret = 
`
public class SystemorphIcons
{
    public const string Provider = "sm";

    ${
        icons.join('\n\t')
    }
}
`
console.log(ret)
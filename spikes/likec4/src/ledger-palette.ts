// Authoritative category colors from the user-provided ColorLedger.drawio.html.
export const ledgerPalette = [
  {
    "key": "objective",
    "label": "Objective",
    "color": "#CE4F7D",
    "foreground": "#FFFFFF"
  },
  {
    "key": "platform",
    "label": "Platform",
    "color": "#00804D",
    "foreground": "#FFFFFF"
  },
  {
    "key": "initiative",
    "label": "Initiative",
    "color": "#30C653",
    "foreground": "#FFFFFF"
  },
  {
    "key": "businessCapability",
    "label": "Business Capability",
    "color": "#0033A0",
    "foreground": "#FFFFFF"
  },
  {
    "key": "businessContext",
    "label": "Business Context",
    "color": "#FF5A8C",
    "foreground": "#FFFFFF"
  },
  {
    "key": "organization",
    "label": "Organization",
    "color": "#2783FF",
    "foreground": "#FFFFFF"
  },
  {
    "key": "application",
    "label": "Application",
    "color": "#0783BD",
    "foreground": "#FFFFFF"
  },
  {
    "key": "interface",
    "label": "Interface",
    "color": "#00AFA5",
    "foreground": "#FFFFFF"
  },
  {
    "key": "dataObject",
    "label": "Data Object",
    "color": "#7753CF",
    "foreground": "#FFFFFF"
  },
  {
    "key": "itComponent",
    "label": "IT Component",
    "color": "#D68965",
    "foreground": "#FFFFFF"
  },
  {
    "key": "provider",
    "label": "Provider",
    "color": "#FF991B",
    "foreground": "#FFFFFF"
  },
  {
    "key": "techCategory",
    "label": "Tech Category",
    "color": "#A84968",
    "foreground": "#FFFFFF"
  }
];
export const ledgerColors = Object.fromEntries(ledgerPalette.map(item => [item.key,item.color]));
export const ledgerToken = (key:string) => `ledger${key[0].toUpperCase()}${key.slice(1)}`;

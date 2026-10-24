# In-app renderer (sample)

A starting point for drawing in-app messages **in your game**, with your own
art, instead of letting the native SDK draw an OS-styled dialog over it.

## What's in here

| File | |
|---|---|
| `JoryioInAppSample.unity` | A scene with the renderer already wired up, its **Panel Prefab** field set to the prefab below. |
| `JoryioInAppPanel.prefab` | The panel: title, body, and two buttons. **This is the thing to restyle** - swap the colours, the font, drop in your own art. |
| `JoryioInAppRenderer.cs` | The glue: receives a message, fills in the panel, reports what happened. |
| `link.xml` | Keeps the Input System's UI module in a stripped release build (see below). |

**Messages arrive only in an iOS or Android build.** In the Editor the SDK is a
no-op (one warning in the console), so pressing Play in the sample scene shows
nothing. Put your SDK keys on the `JoryioInApp` object, build to a device, and
send an in-app campaign to it.

## Restyling

The renderer instantiates whatever is in **Panel Prefab** and fills in four
children, found by name anywhere under its root so you can nest them in your
own layout:

| Child | Component | Used for |
|---|---|---|
| `Title` | `Text` | The message's title; hidden when it has none |
| `Body` | `Text` | The message's body |
| `PrimaryButton` | `Button` with a `Text` label inside | The template for each of the campaign's buttons, copied once per button |
| `SecondaryButton` | `Button` | The close button, shown when the campaign asks for one or has no buttons |

The author's style overrides (`message.Style`) are applied on top of the
prefab where the campaign set them; everything else keeps the prefab's look.
If a child is missing, or the field is empty, the renderer builds a plain panel
in code instead, using the colours in its **Look** fields.

## Taps

uGUI buttons need an EventSystem. The renderer creates one when the scene has
none: with `InputSystemUIInputModule` when the Input System is your active input
handling, otherwise with `StandaloneInputModule`. The Input System module is
found by name, so the sample compiles without that package, and `link.xml`
keeps it from being stripped. If your scene already has an EventSystem, that one
is used.

The `.meta` files are committed on purpose. The scene stores the renderer's
script by GUID, and a regenerated GUID would open the scene with "the
referenced script on this Behaviour is missing" - which does not throw, it just
silently drops the component.

Copy the folder into your project and restyle it. Every visual
choice in it is meant to be replaced; it uses plain uGUI so it has no
dependencies beyond `com.unity.ugui`, which the SDK itself does not require.

## The part to keep

```csharp
JoryioSDK.SetInAppMessageHandler(message => {
    myCanvas.Show(message);
    message.MarkDisplayed();          // and MarkClicked / MarkDismissed
}, canRenderHtml: false);
```

A button tap that closes the message is reported as **both**: `MarkClicked(button)`
and then `MarkDismissed()`, which is what the native SDKs send when they draw the
message themselves.

Installing a handler stops the SDK drawing, and from that point it cannot see
what happened. A message that is never reported **counts as never shown**: no
display in reporting, nothing for revenue attribution to join on, and frequency
caps that never count it. The campaign then reads as delivered to nobody, which
looks like a targeting problem rather than an integration one.

When the renderer is destroyed it calls
`JoryioSDK.ClearInAppMessageHandler(handler)` with the handler it installed, so
messages go back to the native SDK instead of to a handler nobody runs. Passing
the handler removes it only if it is still the installed one: a handler another
script installed after the sample's is left alone.

`canRenderHtml: false` because a uGUI canvas cannot display HTML, which needs a
web view. Campaigns are targeted on what the device reports, and an undeclared
capability is read as "cannot", not "unknown" - so claiming HTML you cannot draw
sends campaigns to players who will never see them.

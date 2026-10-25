# DocMe Mask Painter

Paint black and white masks for your avatar right on the model in the Scene view. White shows an effect, black hides it. The mask is laid out on the mesh's UV map, so it fits any texture slot of the material it is for: a Poiyomi emission mask, a glitter mask, the DocMe Skin Touch mask and so on. Any shader works. For shaders that read a different mask from each colour channel, you can paint one channel at a time.

The window is in English and Japanese.

![A sweater whose two sleeves are filled green by the Mask Painter overlay, while its body stays grey.](https://raw.githubusercontent.com/DocMeVR/mask-painter/main/Docs~/images/example.jpg)

<img src="https://raw.githubusercontent.com/DocMeVR/mask-painter/main/Docs~/images/window.png" width="380" alt="The Mask Painter window with the Mesh, Paint and Save sections and the Language choice at the bottom." /> <img src="https://raw.githubusercontent.com/DocMeVR/mask-painter/main/Docs~/images/window-ja.png" width="380" alt="The same window in Japanese." />

## Install

1. Open my VCC listing page, https://docmevr.github.io/vpm, and press **Add to VCC**. The VRChat Creator Companion then knows my tools and keeps them up to date.
2. In the Creator Companion, open **Manage Project** for your avatar project and add **DocMe Mask Painter**.

Optional, without the Creator Companion: download the `.unitypackage` from the [latest release](https://github.com/DocMeVR/mask-painter/releases/latest) and import it into your project (**Assets > Import Package > Custom Package...**). It needs Unity 2022.3, the version the Creator Companion installs.

## Use

1. Open **Tools > DocMe > Mask Painter**. A selected mesh is already in **Mesh**; if not, drag one in from the Hierarchy (for example Body).
2. Pick the **Material** the mask is for, what it should **Start with** and its **Size**, then press **Start painting**. To keep working on a mask the material already has, set **Start with** to **A texture** and press **Take it from the material**. Leave **UV map** on UV0 unless that material reads its mask from another UV map.
3. In the Scene view, with **Fill pieces** on, click a piece of the model to fill it white; Shift+click fills it black. With **Brush** on, drag to paint white and hold Shift to paint black. The green overlay shows the white parts. Tick **Mirror** to paint both sides at once.
4. Press **Save as...** and pick where the PNG goes. The material's folder is offered first.
5. Under **Save**, press the **Slot** button, type part of the slot's name (for example "emission"), pick it and press **Put into material**.

## Tips

- Ctrl+Z undoes a fill, a brush stroke or a whole mask change, and Ctrl+Y redoes it.
- Alt+drag still turns the Scene view while you paint. **Click does: Off** gives your clicks back to selecting objects.
- The brush size is in centimetres on the model, and a stroke carries across UV seams. The [ and ] keys change the size. The brush paints only the side you aim at, so a stroke on the palm leaves the back of the hand alone.
- **Strength** below 1 builds up greys over several strokes.
- **Show** switches the Scene view between the green **Overlay**, **Mask only** and **Nothing**.
- To paint under clothes, hide them with the eye icon in the Hierarchy: that hides them only in the Scene view.
- A piece is a part of the UV map that hangs together. UV seams split the model into pieces, which is why a click on the chest can fill the whole torso.
- Only the part of the mesh drawn with the chosen material is painted.
- To change a mask you made before, set **Start with** to **A texture** and pick it, or press **Take it from the material**. Every channel and its size are kept. A PNG in your project is saved back into itself: the first **Save** asks before it replaces the file, and offers a new file instead.
- **Paint into** picks the colour channel you paint: **All channels (grey)** for a plain mask, or **Red**, **Green**, **Blue** or **Alpha** alone. The other channels stay as they are, the overlay shows the channel you paint, and Ctrl+Z switches back to the channel a step was painted in.
- A locked Poiyomi material lists only the slots of features that are on. A mask put into a slot that was empty when the material was locked may only show after you unlock and lock the material again.
- A material inside a model file (FBX) cannot keep changes. Extract it to its own file first.
- Closing the window, maximizing the Scene view or a script reload keeps your painting. Unity asks you to save it when you quit.

## What it changes

Nothing on your avatar changes while you paint: the overlay is drawn by the window, not by your materials. **Save as...** writes one PNG, imported as a linear (sRGB off), clamped texture at its full size with high quality compression, because a mask holds amounts rather than colours. A painted alpha is kept as an amount, never as see-through. If a platform has its own format that keeps fewer channels than you painted (red only, for example), that format is turned off so nothing you painted is lost. **Put into material** sets one texture slot of one material, and Ctrl+Z takes it out again.

## How this was made

The code in this repository was written by AI (Claude, by Anthropic). The idea, the design, every feature, the testing on real avatars and the decisions on how it should behave are mine (DocMe). I planned it and directed the work; the AI typed it out. Bug reports and pull requests are welcome, and anything that gets merged goes through my own review.

## License

MIT. See `LICENSE.md`.

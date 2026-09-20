# Equipment V2 — image-generation sources

These ten illustrations were generated with the built-in image generation tool for EagleDeskPet v1.11. No external search art, paid-game screenshots, or third-party characters are included. The PNGs next to this document are the original generated outputs. They are source assets, **not embedded runtime resources**.

## Shared art direction / reusable prompt set

Warm brown outlines, clean 2D cel shading, friendly chunky proportions matching the existing EagleDeskPet eagle. Transparent RGBA background; no floor, cast shadow, background, text, watermark, or UI. Isolated complete objects with generous transparent margins; no cropped edges. Match the reference illustration's line weight and frontal viewpoint. Do not include an eagle in furniture or costume-component assets.

Furniture reference: `Assets/SceneProps/Shop/desk-arcade-back.png`. Computer reference: `Assets/SceneProps/Shop/computer-arcade.png`. Wardrobe and glasses reference: `Assets/mascot-animated-neutral.png`. The shared direction and item briefs below preserve the reusable generation specification; they are not a claim that future image-generation runs will reproduce identical pixels.

| Source | Item brief |
| --- | --- |
| desk-noodle.png | A low wide frontal desk in red-orange with cream trim; two instant-noodle-cup inspired side legs and a little noodle-swirl medallion on the front apron. Empty flat tabletop, no food or other props on top. |
| desk-cloud.png | An ivory/mint cloud-themed desk, scalloped cloud front apron and rounded cloud legs, subtle moon embossing. Empty tabletop and unobstructed center. |
| desk-boardroom.png | A mahogany chairman's desk with restrained gold edging, dark green leather tabletop, a golden baked-pie emblem embossed on the front panel; a visual joke about management promises. |
| computer-server.png | Rear view of an open chunky charcoal laptop, copper horn-like upper corners, mint cooling vents, small ox-nose emblem and hazard stripe on lower edge. Keyboard faces away from the viewer. |
| computer-gold.png | Rear view of an open golden laptop with a winged-coin crest, warm metal highlights and restrained shine. No detached sparkles. |
| computer-ultrabook.png | Rear view of an open very thin champagne-gold laptop with dark emerald leather lid insert and tiny round-spectacles emblem. Thin stable base. |
| ox-atlas.png | Seven disjoint costume pieces in a 3×2 atlas: row 1 denim worker dungaree torso with cream bib, bell clasp and pockets; left blue sleeve with cream cuff; mirrored right sleeve. Row 2 brown ox cap with cream horns and ears, fully empty face opening; separate ox tail; pair of brown work boots. Only clothes, no face, hands, body or mannequin. |
| hero-atlas.png | Seven disjoint pieces in a 3×2 atlas: teal short armored torso with small wing badge, gold trim and red waist belt; left/right teal-gold sleeves; gold winged headband not obscuring the eyes; red round-shouldered cape; pair of teal-gold boots. Original off-duty hero, not an existing franchise costume. No eagle body or face. |
| astronaut-atlas.png | Seven disjoint pieces in a 3×2 atlas: pearl-white/orange spacesuit torso with mint bands, three-button control panel and tiny fish badge; left/right puffy white sleeves with orange cuffs; open white/mint/orange helmet rim with completely empty center, no glass visor; separate oxygen backpack; pair of white/orange boots. No character, skin, hands or face. |
| glasses.png | A pair of delicate warm-gold round wire spectacles, two equal circular rings, narrow bridge and small temple arms; front-facing symmetric view, both lens openings genuinely transparent, no face or eyeballs. |

## Mechanical preparation

`tools/prepare_equipment_art.py` extracts the alpha-connected clothing components (not fixed cells), crops transparent padding and registers the props to the existing 384×346 work-stage coordinates. It does not generate art or invent missing pixels. Desk back/front slices retain the existing tabletop/foreground depth contract. Hidden RGB in fully transparent source pixels is not a visible fringe.

`tools/prepare_outfit_bindings.py` derives per-frame anatomy bindings from the existing animations. The runtime layers the generated clothing onto **each pose** during background preload, retaining the original face, hands and foreground objects. Three new outfits share the default animation binding atlas; the capitalist glasses use the Office atlas. Runtime animation still uses the authored action timings and 60 Hz playback, not a slideshow of the seven component images.

The appearance manifests and the source hashes in the binding archives keep geometry and animation sources coupled explicitly. Regenerate the bindings after replacing the base animation frames; never silently accept stale bindings.

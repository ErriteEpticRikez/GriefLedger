# GriefWarden
A Vintage Story server side mod that logs interactions in the game world with SQLite.

In-Game Commands:
/blocklog -p 1 -r 5
-p flag is for page number
-r flag is for radius
Without radius flag specified, the blocklog will only pull from the looked at block.

/entitylog -p 1 -r 5
flags same as blocklog
Without radius flag specified, will default to radius of 5

/containerlog -p 1
No radius for this, look at the container, or look at the entity (elk/boat/raft), that would have the container

// A struct that holds an array of itself has no finite size; this must be diagnosed, not recursed into.
struct Node
{
    Node children[2];
    float value;
};

template<typename T, uint N>
struct Ring
{
    Ring<int, 4> inner[N];
    T item;
};

[shader("compute")]
[numthreads(1, 1, 1)]
void main()
{
    Node n;
    Ring<int, 4> r;
}

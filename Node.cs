namespace SaveCleaner;

public class Node(object value)
{
    public object Value { get; } = value;
    private Node _parent;
    private Node _child;
    
    public Node Parent
    {
        get => _parent;
        set
        {
            if (_parent == value) return;
            _parent = value;
            value._child = this;
        }
    }

    public Node Child
    {
        get => _child;
        set
        {
            if (_child == value) return;
            _child = value;
            value._parent = this;
        }
    }

    public Node Top => Parent?.Top ?? this;
    public Node Bottom => Child?.Bottom ?? this;
}